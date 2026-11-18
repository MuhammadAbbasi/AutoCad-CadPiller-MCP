// CadBridge: lets an external process (the CADPILLAR MCP) edit the live drawing in CAD&PILLAR 11
// (AutoCAD 2025 OEM). The OEM disables the ActiveX AcadApplication (every call returns E_FAIL, even
// in-process), so COM automation is impossible. This plugin is loaded at startup (registry demand-load,
// see install.ps1) and serves JSON requests on the named pipe \\.\pipe\cadbridge (current user only).
// Each request runs on CAD's main thread through the managed API, directly on the open DWG.
//
// Protocol: one JSON object per line -> one JSON line back: {"ok":true,"result":...} | {"ok":false,"error":"..."}
// Request: {"op": "...", ...}  or  {"op":"batch","ops":[{...},...]}  (batch = one main-thread call, one undo-able tx)
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using SynchronizationContext = System.Threading.SynchronizationContext;

[assembly: ExtensionApplication(typeof(CadBridge.Bridge))]
[assembly: CommandClass(typeof(CadBridge.Bridge))]

namespace CadBridge
{
    public class Bridge : IExtensionApplication
    {
        const string PipeName = "cadbridge";
        static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CadBridge", "cadbridge.log");

        static void Log(string s)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("s") + " " + s + Environment.NewLine); } catch { }
        }

        // Pipe requests are marshalled onto CAD's main (UI) thread through its WPF dispatcher,
        // captured in Initialize, which AutoCAD calls on the main thread.
        static System.Windows.Threading.Dispatcher _disp;

        public void Initialize()
        {
            _disp = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            Log("Initialize on thread " + Environment.CurrentManagedThreadId);
            StartServer();
        }

        static void RunOnMain(Action a) => _disp.BeginInvoke(a);

        public void Terminate() { }

        [CommandMethod("CADBRIDGE")]
        public void Status()
        {
            AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nCadBridge listening on \\\\.\\pipe\\" + PipeName + "\n");
        }

        // ---------------- pipe server ----------------
        static void StartServer()
        {
            var t = new Thread(ServerLoop) { IsBackground = true, Name = "CadBridgePipe" };
            t.Start();
        }

        static void ServerLoop()
        {
            Log("pipe server started");
            while (true)
            {
                try
                {
                    // Several instances: the next listener exists before this client is served, so a
                    // client never finds the pipe missing. CAD work itself stays serialised on the dispatcher.
                    var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    pipe.WaitForConnection();
                    new Thread(() => Serve(pipe)) { IsBackground = true }.Start();
                }
                catch (System.Exception ex) { Log("pipe error: " + ex.Message); Thread.Sleep(200); }
            }
        }

        static void Serve(NamedPipeServerStream pipe)
        {
            int served = 0;
            try
            {
                using (pipe)
                {
                    var reader = new StreamReader(pipe, new UTF8Encoding(false));
                    var writer = new StreamWriter(pipe, new UTF8Encoding(false));
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        writer.Write(Handle(line) + "\n");
                        writer.Flush();
                        served++;
                    }
                }
            }
            catch (System.Exception ex) { Log("serve error after " + served + " requests: " + ex.Message); }
            if (served == 0) Log("connection closed without a request");
        }

        static string Handle(string line)
        {
            var tcs = new TaskCompletionSource<string>();
            RunOnMain(() =>
            {
                try
                {
                    using var jd = JsonDocument.Parse(line);
                    var res = Dispatch(jd.RootElement);
                    tcs.SetResult(JsonSerializer.Serialize(new Dictionary<string, object> { ["ok"] = true, ["result"] = res }));
                }
                catch (System.Exception ex)
                {
                    tcs.SetResult(JsonSerializer.Serialize(new Dictionary<string, object> { ["ok"] = false, ["error"] = ex.GetType().Name + ": " + ex.Message }));
                }
            });
            // ponytail: fixed 120 s wait; a modal dialog in CAD blocks the main thread, so the caller gets a timeout.
            if (!tcs.Task.Wait(TimeSpan.FromSeconds(120)))
                return "{\"ok\":false,\"error\":\"timeout: CAD main thread busy (modal dialog or running command?)\"}";
            return tcs.Task.Result;
        }

        // ---------------- operations (main thread) ----------------
        static object Dispatch(JsonElement r)
        {
            string op = Str(r, "op");
            switch (op)
            {
                case "ping": return "pong";
                case "docs": return AcApp.DocumentManager.Cast<Document>().Select(d => d.Name).ToList();
                case "open":
                    {
                        string path = Path.GetFullPath(Str(r, "path"));
                        // Already open: just activate it (Open() on an open drawing fails with eInvalidContext).
                        var d = AcApp.DocumentManager.Cast<Document>().FirstOrDefault(x =>
                                    string.Equals(Path.GetFullPath(x.Name), path, StringComparison.OrdinalIgnoreCase))
                                ?? AcApp.DocumentManager.Open(path, Bool(r, "read_only", false));
                        AcApp.DocumentManager.MdiActiveDocument = d;
                        return d.Name;
                    }
                case "new":
                    {
                        var d = AcApp.DocumentManager.Add(Str(r, "template", ""));
                        AcApp.DocumentManager.MdiActiveDocument = d;
                        return d.Name;
                    }
                case "command":
                    {
                        // Asynchronous: queued on the command line of the active drawing.
                        Doc().SendStringToExecute(Str(r, "text").TrimEnd('\n') + "\n", true, false, false);
                        return "queued";
                    }
            }
            var doc = Doc();
            if (op == "save")
            {
                string path = Str(r, "path", doc.Name);
                using (doc.LockDocument())
                    doc.Database.SaveAs(path, true, DwgVersion.Current, doc.Database.SecurityParameters);
                return path;
            }
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                object res;
                if (op == "batch")
                {
                    var outs = new List<object>();
                    int i = 0;
                    foreach (var sub in r.GetProperty("ops").EnumerateArray())
                    {
                        try { outs.Add(Op(doc, tr, sub)); }
                        catch (System.Exception ex) { throw new InvalidOperationException("op #" + i + " (" + Str(sub, "op") + "): " + ex.Message, ex); }
                        i++;
                    }
                    res = outs;
                }
                else res = Op(doc, tr, r);
                tr.Commit();
                return res;
            }
        }

        static Document Doc()
        {
            return AcApp.DocumentManager.MdiActiveDocument ?? throw new InvalidOperationException("no drawing open");
        }

        static object Op(Document doc, Transaction tr, JsonElement r)
        {
            var db = doc.Database;
            string op = Str(r, "op");
            switch (op)
            {
                case "info":
                    {
                        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                        var layers = new List<object>();
                        foreach (ObjectId id in lt)
                        {
                            var l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                            layers.Add(new { name = l.Name, color = l.Color.ColorIndex, off = l.IsOff, frozen = l.IsFrozen, locked = l.IsLocked });
                        }
                        int n = 0;
                        foreach (ObjectId _ in Ms(tr, db, OpenMode.ForRead)) n++;
                        var blocks = new List<string>();
                        foreach (ObjectId id in (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead))
                        {
                            var b = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                            if (!b.IsLayout && !b.IsAnonymous && !b.IsFromExternalReference) blocks.Add(b.Name);
                        }
                        return new
                        {
                            name = doc.Name, insunits = db.Insunits.ToString(), entities = n,
                            extmin = P(db.Extmin), extmax = P(db.Extmax), layers, blocks
                        };
                    }
                case "list":
                    {
                        string layer = Str(r, "layer", null), type = Str(r, "type", null);
                        int limit = Int(r, "limit", 500);
                        var outl = new List<object>();
                        foreach (ObjectId id in Ms(tr, db, OpenMode.ForRead))
                        {
                            var e = (Entity)tr.GetObject(id, OpenMode.ForRead);
                            if (layer != null && !string.Equals(e.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;
                            if (type != null && !string.Equals(id.ObjectClass.DxfName, type, StringComparison.OrdinalIgnoreCase)) continue;
                            outl.Add(Describe(e));
                            if (outl.Count >= limit) break;
                        }
                        return outl;
                    }
                case "get": return Describe((Entity)tr.GetObject(H(db, Str(r, "handle")), OpenMode.ForRead));
                case "layer":
                    {
                        string name = Str(r, "name");
                        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                        LayerTableRecord l;
                        if (lt.Has(name)) l = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForWrite);
                        else
                        {
                            lt.UpgradeOpen();
                            l = new LayerTableRecord { Name = name };
                            lt.Add(l); tr.AddNewlyCreatedDBObject(l, true);
                        }
                        if (r.TryGetProperty("color", out _)) l.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)Int(r, "color", 7));
                        if (r.TryGetProperty("lineweight", out _)) l.LineWeight = (LineWeight)Int(r, "lineweight", -3); // hundredths of mm: 35 = 0.35
                        if (r.TryGetProperty("linetype", out _)) l.LinetypeObjectId = Linetype(tr, db, Str(r, "linetype"));
                        return name;
                    }
                case "line":
                    return Add(tr, db, r, new Line(new Point3d(D(r, "x1"), D(r, "y1"), 0), new Point3d(D(r, "x2"), D(r, "y2"), 0)));
                case "pline":
                    {
                        var pl = new Polyline();
                        int i = 0;
                        foreach (var p in r.GetProperty("points").EnumerateArray())
                        {
                            double bulge = p.GetArrayLength() > 2 ? p[2].GetDouble() : 0;
                            pl.AddVertexAt(i++, new Point2d(p[0].GetDouble(), p[1].GetDouble()), bulge, 0, 0);
                        }
                        pl.Closed = Bool(r, "closed", false);
                        return Add(tr, db, r, pl);
                    }
                case "circle":
                    return Add(tr, db, r, new Circle(new Point3d(D(r, "cx"), D(r, "cy"), 0), Vector3d.ZAxis, D(r, "r")));
                case "arc":
                    return Add(tr, db, r, new Arc(new Point3d(D(r, "cx"), D(r, "cy"), 0), D(r, "r"),
                        Deg(D(r, "start")), Deg(D(r, "end"))));
                case "text":
                    {
                        var t = new MText
                        {
                            Location = new Point3d(D(r, "x"), D(r, "y"), 0),
                            TextHeight = D(r, "height"),
                            Contents = Str(r, "text"),
                            Rotation = Deg(Dbl(r, "rotation", 0)),
                            Attachment = (AttachmentPoint)Int(r, "attach", (int)AttachmentPoint.MiddleCenter),
                        };
                        if (r.TryGetProperty("width", out _)) t.Width = D(r, "width");
                        return Add(tr, db, r, t);
                    }
                case "dim":
                    {
                        // aligned dimension between (x1,y1)-(x2,y2), dimension line through (dx,dy)
                        var dm = new AlignedDimension(new Point3d(D(r, "x1"), D(r, "y1"), 0), new Point3d(D(r, "x2"), D(r, "y2"), 0),
                            new Point3d(D(r, "dx"), D(r, "dy"), 0), Str(r, "text", ""), db.Dimstyle);
                        string h = Add(tr, db, r, dm);
                        if (r.TryGetProperty("scale", out _)) dm.Dimscale = D(r, "scale"); // overall dim scale (DIMSCALE)
                        return h;
                    }
                case "hatch":
                    {
                        // boundary: closed polygon points [[x,y],...]; pattern default SOLID
                        var bnd = new Polyline();
                        int i = 0;
                        foreach (var p in r.GetProperty("points").EnumerateArray())
                            bnd.AddVertexAt(i++, new Point2d(p[0].GetDouble(), p[1].GetDouble()), 0, 0, 0);
                        bnd.Closed = true;
                        bnd.SetDatabaseDefaults(db);
                        if (!_target.IsNull) bnd.Layer = "0";
                        ApplyProps(tr, db, r, bnd); // a kept boundary sits on the hatch's layer
                        var ms = Target(tr, db);
                        ms.AppendEntity(bnd); tr.AddNewlyCreatedDBObject(bnd, true);
                        var h = new Hatch();
                        h.SetDatabaseDefaults(db);
                        if (!_target.IsNull) h.Layer = "0";
                        ApplyProps(tr, db, r, h);
                        ms.AppendEntity(h); tr.AddNewlyCreatedDBObject(h, true);
                        // scale/angle must be set BEFORE SetHatchPattern, or the pattern is built at scale 1
                        if (r.TryGetProperty("scale", out _)) h.PatternScale = D(r, "scale");
                        if (r.TryGetProperty("angle", out _)) h.PatternAngle = Deg(D(r, "angle"));
                        h.SetHatchPattern(HatchPatternType.PreDefined, Str(r, "pattern", "SOLID"));
                        h.Associative = false;
                        h.AppendLoop(HatchLoopTypes.Outermost, new ObjectIdCollection { bnd.ObjectId });
                        h.EvaluateHatch(true);
                        if (!Bool(r, "keep_boundary", false)) bnd.Erase();
                        return h.Handle.ToString();
                    }
                case "insert":
                    {
                        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        string name = Str(r, "name");
                        if (!bt.Has(name)) throw new ArgumentException("block not defined: " + name);
                        var br = new BlockReference(new Point3d(D(r, "x"), D(r, "y"), 0), bt[name])
                        {
                            Rotation = Deg(Dbl(r, "rotation", 0)), ScaleFactors = new Scale3d(Dbl(r, "scale", 1))
                        };
                        return Add(tr, db, r, br);
                    }
                case "block_define":
                    {
                        // {"name", "entities":[create ops in block coordinates, base point = 0,0]}; redefines in place
                        string name = Str(r, "name");
                        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        BlockTableRecord btr;
                        if (bt.Has(name))
                        {
                            btr = (BlockTableRecord)tr.GetObject(bt[name], OpenMode.ForWrite);
                            foreach (ObjectId id in btr) tr.GetObject(id, OpenMode.ForWrite).Erase();
                        }
                        else
                        {
                            bt.UpgradeOpen();
                            btr = new BlockTableRecord { Name = name, Origin = Point3d.Origin };
                            bt.Add(btr); tr.AddNewlyCreatedDBObject(btr, true);
                        }
                        if (!_target.IsNull) throw new InvalidOperationException("nested block_define");
                        _target = btr.ObjectId;
                        try { foreach (var sub in r.GetProperty("entities").EnumerateArray()) Op(doc, tr, sub); }
                        finally { _target = ObjectId.Null; }
                        return name;
                    }
                case "block_import":
                    {
                        // {"path": "x.dwg", "name"?}: the DWG's model space becomes a block definition
                        string path = Str(r, "path");
                        string name = Str(r, "name", Path.GetFileNameWithoutExtension(path));
                        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        if (!bt.Has(name) || Bool(r, "overwrite", false))
                        {
                            using var src = new Database(false, true);
                            src.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
                            db.Insert(name, src, true);
                        }
                        return BlockSummary(tr, db, name);
                    }
                case "block_info": return BlockSummary(tr, db, Str(r, "name"));
                case "delete":
                    {
                        int n = 0;
                        foreach (var h in r.GetProperty("handles").EnumerateArray())
                        { ((Entity)tr.GetObject(H(db, h.GetString()), OpenMode.ForWrite)).Erase(); n++; }
                        return n;
                    }
                case "move":
                case "copy":
                case "rotate":
                    {
                        Matrix3d m = op == "rotate"
                            ? Matrix3d.Rotation(Deg(D(r, "angle")), Vector3d.ZAxis, new Point3d(D(r, "bx"), D(r, "by"), 0))
                            : Matrix3d.Displacement(new Vector3d(D(r, "dx"), D(r, "dy"), 0));
                        var outh = new List<string>();
                        var ms = op == "copy" ? Ms(tr, db, OpenMode.ForWrite) : null;
                        foreach (var hh in r.GetProperty("handles").EnumerateArray())
                        {
                            var e = (Entity)tr.GetObject(H(db, hh.GetString()), OpenMode.ForWrite);
                            if (op == "copy")
                            {
                                var c = (Entity)e.Clone(); c.TransformBy(m);
                                ms.AppendEntity(c); tr.AddNewlyCreatedDBObject(c, true);
                                outh.Add(c.Handle.ToString());
                            }
                            else { e.TransformBy(m); outh.Add(e.Handle.ToString()); }
                        }
                        return outh;
                    }
                case "set":
                    {
                        var e = (Entity)tr.GetObject(H(db, Str(r, "handle")), OpenMode.ForWrite);
                        ApplyProps(tr, db, r, e);
                        if (e is MText mt && r.TryGetProperty("text", out _)) mt.Contents = Str(r, "text");
                        if (e is DBText dt && r.TryGetProperty("text", out _)) dt.TextString = Str(r, "text");
                        return e.Handle.ToString();
                    }
                case "getvar": return AcApp.GetSystemVariable(Str(r, "name"))?.ToString();
                case "setvar":
                    {
                        var cur = AcApp.GetSystemVariable(Str(r, "name"));
                        object v = cur is short ? (object)(short)Int(r, "value", 0)
                                 : cur is int ? Int(r, "value", 0)
                                 : cur is double ? Dbl(r, "value", 0)
                                 : (object)r.GetProperty("value").ToString();
                        AcApp.SetSystemVariable(Str(r, "name"), v);
                        return true;
                    }
                default: throw new ArgumentException("unknown op: " + op);
            }
        }

        // ---------------- helpers ----------------
        static BlockTableRecord Ms(Transaction tr, Database db, OpenMode mode)
        {
            return (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), mode);
        }

        // Where creates go: model space, or the block definition being built by "block_define".
        static ObjectId _target = ObjectId.Null;
        static BlockTableRecord Target(Transaction tr, Database db)
        {
            return _target.IsNull ? Ms(tr, db, OpenMode.ForWrite) : (BlockTableRecord)tr.GetObject(_target, OpenMode.ForWrite);
        }

        static string Add(Transaction tr, Database db, JsonElement r, Entity e)
        {
            e.SetDatabaseDefaults(db);
            if (!_target.IsNull) e.Layer = "0"; // block content on 0 follows the INSERT's layer
            ApplyProps(tr, db, r, e);
            var ms = Target(tr, db);
            ms.AppendEntity(e);
            tr.AddNewlyCreatedDBObject(e, true);
            return e.Handle.ToString();
        }

        static object BlockSummary(Transaction tr, Database db, string name)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (!bt.Has(name)) throw new ArgumentException("block not defined: " + name);
            var btr = (BlockTableRecord)tr.GetObject(bt[name], OpenMode.ForRead);
            Extents3d? ext = null;
            int n = 0;
            var types = new Dictionary<string, int>();
            foreach (ObjectId id in btr)
            {
                n++;
                types[id.ObjectClass.DxfName] = types.TryGetValue(id.ObjectClass.DxfName, out int k) ? k + 1 : 1;
                try
                {
                    var e = (Entity)tr.GetObject(id, OpenMode.ForRead);
                    var ge = e.GeometricExtents;
                    if (ext == null) ext = ge; else { var x = ext.Value; x.AddExtents(ge); ext = x; }
                }
                catch { }
            }
            return new
            {
                name, entities = n, types, insunits = btr.Units.ToString(),
                min = ext == null ? null : P(ext.Value.MinPoint), max = ext == null ? null : P(ext.Value.MaxPoint)
            };
        }

        static void ApplyProps(Transaction tr, Database db, JsonElement r, Entity e)
        {
            if (r.TryGetProperty("layer", out _))
            {
                string name = Str(r, "layer");
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (!lt.Has(name)) // create missing layers instead of failing
                {
                    lt.UpgradeOpen();
                    var l = new LayerTableRecord { Name = name };
                    lt.Add(l); tr.AddNewlyCreatedDBObject(l, true);
                }
                e.Layer = name;
            }
            if (r.TryGetProperty("color", out _))
                e.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)Int(r, "color", 256));
        }

        static ObjectId Linetype(Transaction tr, Database db, string name)
        {
            var lt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            if (!lt.Has(name)) db.LoadLineTypeFile(name, "acadiso.lin");
            return lt[name];
        }

        static object Describe(Entity e)
        {
            var d = new Dictionary<string, object>
            {
                ["handle"] = e.Handle.ToString(), ["type"] = e.GetRXClass().DxfName, ["layer"] = e.Layer, ["color"] = e.Color.ColorIndex
            };
            try { var ex = e.GeometricExtents; d["min"] = P(ex.MinPoint); d["max"] = P(ex.MaxPoint); } catch { }
            switch (e)
            {
                case Line l: d["start"] = P(l.StartPoint); d["end"] = P(l.EndPoint); break;
                case Polyline pl:
                    d["closed"] = pl.Closed;
                    d["points"] = Enumerable.Range(0, pl.NumberOfVertices).Select(i => new[] { pl.GetPoint2dAt(i).X, pl.GetPoint2dAt(i).Y, pl.GetBulgeAt(i) }).ToList();
                    break;
                case Circle c: d["center"] = P(c.Center); d["r"] = c.Radius; break;
                case Arc a: d["center"] = P(a.Center); d["r"] = a.Radius; d["start"] = a.StartAngle * 180 / Math.PI; d["end"] = a.EndAngle * 180 / Math.PI; break;
                case MText m: d["text"] = m.Contents; d["at"] = P(m.Location); d["height"] = m.TextHeight; break;
                case DBText t: d["text"] = t.TextString; d["at"] = P(t.Position); d["height"] = t.Height; break;
                case BlockReference b: d["name"] = b.Name; d["at"] = P(b.Position); d["rotation"] = b.Rotation * 180 / Math.PI; break;
            }
            return d;
        }

        static ObjectId H(Database db, string handle)
        {
            if (!db.TryGetObjectId(new Handle(Convert.ToInt64(handle, 16)), out ObjectId id) || id.IsErased)
                throw new ArgumentException("unknown handle: " + handle);
            return id;
        }

        static double[] P(Point3d p) => new[] { Math.Round(p.X, 6), Math.Round(p.Y, 6), Math.Round(p.Z, 6) };
        static double Deg(double a) => a * Math.PI / 180.0;
        static string Str(JsonElement r, string k) => r.TryGetProperty(k, out var v) ? v.GetString() : throw new ArgumentException("missing '" + k + "'");
        static string Str(JsonElement r, string k, string dflt) => r.TryGetProperty(k, out var v) ? v.GetString() : dflt;
        static double D(JsonElement r, string k) => r.TryGetProperty(k, out var v) ? v.GetDouble() : throw new ArgumentException("missing '" + k + "'");
        static double Dbl(JsonElement r, string k, double dflt) => r.TryGetProperty(k, out var v) ? v.GetDouble() : dflt;
        static int Int(JsonElement r, string k, int dflt) => r.TryGetProperty(k, out var v) ? v.GetInt32() : dflt;
        static bool Bool(JsonElement r, string k, bool dflt) => r.TryGetProperty(k, out var v) ? v.GetBoolean() : dflt;
    }
}
