using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BalancePlug;
using BalancePlug.Core;

internal static class UiPerformance
{
    public static int Run()
    {
        if (Environment.GetEnvironmentVariable("BALANCEPLUG_DATA_DIR") is null) throw new Exception("Explicit isolated data directory required");
        var result = 1;
        var thread = new Thread(() =>
        {
            var app = TestApplication.Create(); var window = new MainWindow(); app.MainWindow = window; window.Show();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(async () =>
            {
                try
                {
                    await Task.Delay(1000);
                    var settings = (AppSettings)UiSmoke.Field(window,"settings");
                    var selected = settings.ActiveMascotId;
                    if (selected is not null) { UiSmoke.Invoke(window,"SelectMascot",new object[] {null!}); UiSmoke.Invoke(window,"SelectMascot",selected); }
                    for(var i=0;i<3;i++) { UiSmoke.Invoke(window,"OpenMenu"); await Task.Delay(150); UiSmoke.Invoke(window,"CloseMenu"); await Task.Delay(100); }
                    UiSmoke.Invoke(window,"OpenSizes"); await Task.Delay(150); ((Window)UiSmoke.Field(window,"sizeWindow")).Close();
                    UiSmoke.Invoke(window,"OpenAccount"); await Task.Delay(150); ((Window)UiSmoke.Field(window,"accountWindow")).Close();
                    var delta = UiSmoke.Field(window,"decrease"); delta.GetType().GetMethod("Show")!.Invoke(delta,new object[] {.005m});
                    await Task.Delay(2300); await Task.Delay(10000);
                    using var process = Process.GetCurrentProcess(); process.Refresh();
                    var cpu = process.TotalProcessorTime.TotalSeconds; var clock = Stopwatch.StartNew();
                    var samples = new System.Collections.Generic.List<object>(); var maximum = 0d;
                    for(var i=0;i<12;i++)
                    {
                        process.Refresh(); var mb = process.WorkingSet64 / 1048576d; maximum = Math.Max(maximum,mb);
                        samples.Add(new { elapsedSeconds=clock.Elapsed.TotalSeconds,workingSetMB=mb,privateMB=process.PrivateMemorySize64/1048576d,handles=process.HandleCount });
                        await Task.Delay(5000);
                    }
                    process.Refresh(); var singleCpu = (process.TotalProcessorTime.TotalSeconds-cpu)/clock.Elapsed.TotalSeconds*100;
                    var report = new { status="completed",completedAt=DateTimeOffset.Now,durationSeconds=clock.Elapsed.TotalSeconds,maxWorkingSetMB=maximum,
                        averageCpuSingleCorePercent=singleCpu,averageCpuMachinePercent=singleCpu/Environment.ProcessorCount,
                        targetsPass=maximum<=100 && singleCpu/Environment.ProcessorCount<=1,samples,
                        notes="Same production MainWindow/API, hosted by test executable. Warmed imported image, menu, sliders, account dialog and animation. No forced GC or working-set trimming." };
                    var output = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,"outputs","ui-performance-v1.1.json"));
                    File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions {WriteIndented=true}));
                    Console.WriteLine("WARM UI maxMB="+maximum.ToString("F2")+" cpuMachine="+(singleCpu/Environment.ProcessorCount).ToString("F3")+" pass="+report.targetsPass);
                    result=report.targetsPass ? 0 : 1;
                }
                catch(Exception ex) { Console.WriteLine("FAIL UI PERFORMANCE "+ex); }
                finally { window.Close(); Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.ApplicationIdle); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return result;
    }
}
