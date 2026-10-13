using System;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// No global clipboard writes: all inputs and writers are synthetic.
class ClipboardRegression
{
    static Type program;
    static int checks;
    static object Call(string name, params object[] args)
    {
        return program.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args);
    }
    static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        checks++;
    }
    [STAThread]
    static void Main(string[] args)
    {
        program = Assembly.LoadFrom(args[0]).GetType("ClipBridge.Program");
        Type mediaType = program.GetNestedType("MediaClipboard", BindingFlags.NonPublic);
        object media = Activator.CreateInstance(mediaType, true);
        try
        {
            string[] files = { @"C:\images\中文 photo.png", @"C:\audio\song.mp3", @"C:\video\clip.mp4", @"C:\docs\notes.txt", @"C:\folder" };
            DataObject source = new DataObject();
            source.SetData(DataFormats.FileDrop, false, files);
            byte[] cutEffect = { 2, 0, 0, 0 };
            source.SetData("Preferred DropEffect", false, new MemoryStream(cutEffect));
            source.SetData("custom/source", false, "keep me");
            DataObject rich = (DataObject)Call("SnapshotFormats", source, media);
            mediaType.GetField("Rich").SetValue(media, rich);
            Call("AddPaths", media, files);
            DataObject paths = (DataObject)mediaType.GetField("Paths").GetValue(media);
            string[] restored = (string[])rich.GetData(DataFormats.FileDrop, false);
            Check(restored.Length == files.Length && string.Join("|", restored) == string.Join("|", files), "Mixed selection lost files/order");
            Check((string)paths.GetData(DataFormats.UnicodeText) == string.Join(Environment.NewLine, files), "CLI lost paths/Unicode/spaces");
            Check(!paths.GetDataPresent(DataFormats.FileDrop, false) && !paths.GetDataPresent("PNG", false), "CLI exposed rich file/image formats");
            Check((string)rich.GetData("custom/source", false) == "keep me", "Source format lost");
            Check(((MemoryStream)rich.GetData("Preferred DropEffect", false)).ToArray()[0] == 2, "Cut metadata changed");
            Check(!object.ReferenceEquals(source.GetData("Preferred DropEffect", false), rich.GetData("Preferred DropEffect", false)), "Source stream not snapshotted");
            Check((bool)Call("IsMediaFile", "CLIP.MP4") && (bool)Call("IsMediaFile", "audio.FLAC"), "Media extension support failed");
            Check(!(bool)Call("IsMediaFile", "notes.txt"), "Unrelated files incorrectly handled");
            Check((bool)Call("IsPathProcess", "cursor", "pwsh, Cursor"), "Process matching failed");
            Check(!(bool)Call("IsPathProcess", "CursorHelper", "Cursor"), "Process prefix misclassified");

            // Rich image data must remain usable across repeated app switches.
            object config = Activator.CreateInstance(program.Assembly.GetType("ClipBridge.AppConfig"));
            using (Bitmap image = new Bitmap(2, 2))
            using (MemoryStream png = new MemoryStream())
            {
                image.SetPixel(0, 0, Color.Red);
                image.Save(png, ImageFormat.Png);
                Call("AddImage", media, png.ToArray(), config);
            }
            Check(((MemoryStream)rich.GetData("PNG", false)).Length > 0, "PNG backing stream disposed early");
            Check(((Image)rich.GetData(DataFormats.Bitmap)).GetPixelForTest() == Color.Red.ToArgb(), "Image backing stream lost");
            string rtf = System.Text.Encoding.ASCII.GetString(((MemoryStream)rich.GetData(DataFormats.Rtf, false)).ToArray());
            Check(rtf.Contains("\\pngblip") && rtf.Contains("\\picw2\\pich2"), "Office image representation missing");

            uint sequence = 7;
            int writes = 0, waits = 0;
            Func<uint> readSequence = delegate { return sequence; };
            Action<DataObject> write = delegate { writes++; };
            Action<int> wait = delegate { waits++; };
            Check(!(bool)Call("TryPublishClipboard", paths, (uint)6, readSequence, write, wait, 8, 25) && writes == 0, "Stale clipboard was overwritten");
            Check((bool)Call("TryPublishClipboard", paths, (uint)7, readSequence, write, wait, 8, 25) && writes == 1, "Valid publication failed");
            writes = 0;
            write = delegate { writes++; throw new ExternalException("busy"); };
            wait = delegate { waits++; sequence = 8; };
            Check(!(bool)Call("TryPublishClipboard", paths, (uint)7, readSequence, write, wait, 8, 25) && writes == 1, "Retry overwrote a newer copy");
            sequence = 9;
            writes = 0;
            write = delegate { writes++; if (writes == 1) throw new ExternalException("busy"); };
            wait = delegate { waits++; };
            Check((bool)Call("TryPublishClipboard", paths, (uint)9, readSequence, write, wait, 8, 25) && writes == 2, "Busy clipboard retry failed");
        }
        finally { ((IDisposable)media).Dispose(); }
        Console.WriteLine("PASS: " + checks + " clipboard regression checks; global clipboard untouched.");
    }
}

static class ImageChecks
{
    public static int GetPixelForTest(this Image image)
    {
        using (Bitmap bitmap = new Bitmap(image)) return bitmap.GetPixel(0, 0).ToArgb();
    }
}
