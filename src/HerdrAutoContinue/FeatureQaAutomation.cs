#if DEBUG
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Watchdog.Infrastructure;
namespace HerdrAutoContinue;
internal static class FeatureQaAutomation
{
    public static async Task RunAsync(DesktopApplicationController host, MainViewModel model, AppSettingsController settings, string directory)
    {
        Directory.CreateDirectory(directory);var result=new Dictionary<string,object?>();
        try
        {
            host.ShowMainWindow();var window=host.QaWindow!;
            if(Environment.GetEnvironmentVariable("WATCHDOG_QA_SCENARIO")=="features-restart")
            {
                Require(!settings.Current.Settings.ProxyRecoveryEnabled && settings.Current.Settings.AgentRecoveryEnabled,"Restart lost flags");
                Require(!model.QaSnapshot.ProxyRecoveryEnabled && model.QaSnapshot.AgentRecoveryEnabled,"Runtime flags not restored");
                result["restartRestored"]=true;
            }
            else
            {
                Require(settings.Current.Settings.ProxyRecoveryEnabled && settings.Current.Settings.AgentRecoveryEnabled,"Defaults must be enabled");
                var panel=await Open();Find<CheckBox>(panel,"proxy-recovery-enabled").IsChecked=false;Find<CheckBox>(panel,"agent-recovery-enabled").IsChecked=false;
                Click(panel,"settings-cancel");await Wait(()=>host.QaSettingsWindow is null);
                Require(model.QaSnapshot.ProxyRecoveryEnabled && model.QaSnapshot.AgentRecoveryEnabled,"Cancel changed runtime");
                foreach(var (proxy,agent) in new[]{(true,false),(false,false),(true,true),(false,true)})
                {
                    panel=await Open();Find<CheckBox>(panel,"proxy-recovery-enabled").IsChecked=proxy;Find<CheckBox>(panel,"agent-recovery-enabled").IsChecked=agent;
                    await Task.Delay(150);Capture(panel,$"settings-{proxy}-{agent}.png");
                    panel.Width=480;panel.Height=350;await Task.Delay(150);Find<CheckBox>(panel,"agent-recovery-enabled").BringIntoView();await Task.Delay(150);Capture(panel,$"minimum-{proxy}-{agent}.png");
                    Click(panel,"settings-apply");await Wait(()=>host.QaSettingsWindow is null);
                    Require(settings.Current.Settings.ProxyRecoveryEnabled==proxy && settings.Current.Settings.AgentRecoveryEnabled==agent,"Save lost flags");
                    Require(model.QaSnapshot.ProxyRecoveryEnabled==proxy && model.QaSnapshot.AgentRecoveryEnabled==agent && model.QaSnapshot.Running,"Runtime flags or service state incorrect");
                }
                result["fourCombinationsAppliedLive"]=true;result["cancelPreserved"]=true;
            }
            await Task.Delay(100);Capture(window,"main.png");result["success"]=true;
            async Task<AppSettingsWindow> Open(){Click(window,"app-settings");await Wait(()=>host.QaSettingsWindow is not null);return host.QaSettingsWindow!;}
            void Capture(Window w,string file){using var bitmap=new RenderTargetBitmap(new PixelSize((int)w.ClientSize.Width,(int)w.ClientSize.Height));bitmap.Render(w);bitmap.Save(Path.Combine(directory,file),PngBitmapEncoderOptions.Default);}
        }
        catch(Exception e){result["success"]=false;result["error"]=e.ToString();}
        finally{host.QaStopped+=()=>File.WriteAllText(Path.Combine(directory,"result.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));await host.QuitAsync();}
    }
    private static T Find<T>(Window w,string id) where T:Control=>w.GetVisualDescendants().OfType<T>().Single(c=>AutomationProperties.GetAutomationId(c)==id);
    private static void Click(Window w,string id)=>Find<Button>(w,id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private static async Task Wait(Func<bool> condition){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));while(!condition())await Task.Delay(30,timeout.Token);}
}
#endif
