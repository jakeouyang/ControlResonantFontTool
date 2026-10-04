namespace ControlResonantFontTool;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length > 0)
        {
            try
            {
                if (args[0] == "--self-test" && args.Length == 3) { SelfTest.Run(args[1], args[2]); return 0; }
                if (args[0] == "--render" && args.Length == 2)
                {
                    using var form = new MainForm(); form.Show(); Application.DoEvents();
                    using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, form.ClientRectangle);
                    bitmap.Save(args[1]); return 0;
                }
                throw new ArgumentException("Unknown command.");
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "validation-error.txt"), ex.ToString()); return 1; }
        }
        Application.Run(new MainForm()); return 0;
    }
}
