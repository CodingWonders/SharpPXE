// SharpPXE.Gui - WinForms front end for SharpPXE.exe
//
//   SharpPXE.Gui.exe [--server <path to SharpPXE.exe>] [--config <path to SharpPXE.ini>]
//
// By default both are looked up next to SharpPXE.Gui.exe.

namespace SharpPXE.Gui;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Applies the csproj settings: high-DPI mode, visual styles, default font.
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args));
    }
}
