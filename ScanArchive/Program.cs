namespace ScanArchive;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        if (Environment.GetCommandLineArgs().Contains("--self-test")) { SelfTest.Run(); return; }
        var args=Environment.GetCommandLineArgs();
        if(args.Contains("--ui-smoke")){MainWindow.UiSmoke(args[Array.IndexOf(args,"--ui-smoke")+1]);return;}
        using var instance=new Mutex(true,"Local\\ScanArchive.Desktop",out bool created);
        if(!created){MessageBox.Show("Scan Archive 已在运行，请从任务栏打开现有窗口。","Scan Archive");return;}
        Application.Run(new MainWindow());
    }    
}
