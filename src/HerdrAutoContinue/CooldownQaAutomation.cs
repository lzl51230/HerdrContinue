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
internal static class CooldownQaAutomation
{
    public static async Task RunAsync(DesktopApplicationController host, MainViewModel model, AppSettingsController settings, string directory)
    {
        Directory.CreateDirectory(directory);var result=new Dictionary<string,object?>();
        try
        {
            host.ShowMainWindow();var window=host.QaWindow!;
            if(Environment.GetEnvironmentVariable("WATCHDOG_QA_SCENARIO")=="cooldown-restart")
            {Require(settings.Current.Settings.CooldownSeconds==10 && model.CooldownDescription.Contains("10 秒"),"Restart lost cooldown");result["restartRestored"]=true;}
            else
            {
                Require(settings.Current.Settings.CooldownSeconds==5,"Default must be 5");
                var panel=await Open();var input=Find<NumericUpDown>(panel,"cooldown-seconds");Require(input.Value==5 && input.Minimum==1 && input.Maximum==300,"Incorrect control limits");
                input.Value=10;Click(panel,"settings-cancel");await Wait(()=>host.QaSettingsWindow is null);Require(settings.Current.Settings.CooldownSeconds==5,"Cancel changed setting");
                panel=await Open();Find<NumericUpDown>(panel,"cooldown-seconds").Value=10;
                await Task.Delay(200);Capture(panel,"settings-default-size.png");
                panel.Width=480;panel.Height=350;await Task.Delay(200);Find<NumericUpDown>(panel,"cooldown-seconds").BringIntoView();await Task.Delay(200);Capture(panel,"settings-minimum-size.png");
                Click(panel,"settings-apply");await Wait(()=>host.QaSettingsWindow is null);
                Require(settings.Current.Settings.CooldownSeconds==10 && model.CooldownDescription.Contains("10 秒"),"Save did not apply live");
                panel=await Open();Require(Find<NumericUpDown>(panel,"cooldown-seconds").Value==10,"Reopen lost setting");Click(panel,"settings-cancel");await Wait(()=>host.QaSettingsWindow is null);
                result["defaultAndRange"]=true;result["cancelPreserved"]=true;result["savedAndAppliedLive"]=true;result["reopenRestored"]=true;
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
