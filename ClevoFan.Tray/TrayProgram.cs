using System.Windows.Forms;

namespace ClevoFan.Tray;

internal static class TrayProgram
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            using var app = new TrayApp();
            Application.Run(app);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Clevo Fan couldn't start:\n\n{ex.Message}\n\n" +
                "Checklist:\n" +
                "  • Running as Administrator?\n" +
                "  • PawnIO installed (pawnio.eu)?\n" +
                "  • LpcACPIEC.bin next to ClevoFanTray.exe?",
                "Clevo Fan", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
