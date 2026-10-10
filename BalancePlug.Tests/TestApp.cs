using System;
using System.Windows;

// Load production styles but do not run App's automatic Startup window creation.
internal static class TestApplication
{
    public static Application Create()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/BalancePlug;component/UiStyles.xaml",UriKind.Relative) });
        return app;
    }
}
