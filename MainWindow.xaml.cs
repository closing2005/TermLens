using System;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TermLens;

public partial class MainWindow : Window
{
    private readonly App app;
    private CancellationTokenSource? testing;
    public MainWindow(App app)
    {
        this.app = app;
        InitializeComponent();
        UrlInput.Text = app.Settings.BaseUrl;
        KeyInput.Password = app.Settings.ApiKey;
        ModelInput.Text = app.Settings.Model;
        if (string.IsNullOrEmpty(app.Settings.ApiKey)) Tabs.SelectedIndex = 1;
        Closing += (_, e) =>
        {
            if (app.Exiting) return;
            e.Cancel = true;
            testing?.Cancel();
            Hide();
        };
    }
    public void SetStatus(string text) => Status.Text = text;
    public void SetPaused(bool paused) => PauseButton.Content = paused ? "○ 划词已暂停" : "● 划词已开启";
    public void OpenSettings() => Tabs.SelectedIndex = 1;
    private void Pause_Click(object sender, RoutedEventArgs e) => app.TogglePause();
    private void Query_Click(object sender, RoutedEventArgs e) => Query();
    private void Term_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { Query(); e.Handled = true; } }
    private void Example_Click(object sender, RoutedEventArgs e)
    {
        TermInput.Text = ((Button)sender).Content.ToString();
        TermInput.Focus();
    }
    private void Query()
    {
        try
        {
            var term = QueryRules.Validate(TermInput.Text, ContextInput.Text);
            Native.GetCursorPos(out var anchor);
            app.Lookup(term, ContextInput.Text, anchor);
        }
        catch (UserError ex) { SetStatus(ex.Message); }
    }
    private ApiSettings ReadSettings() => new(UrlInput.Text.Trim(), KeyInput.Password.Trim(), ModelInput.Text.Trim());
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadSettings();
            SettingsStore.Save(settings);
            app.Settings = settings;
            SettingsStatus.Text = "已保存。现在可以划词或手动查询。";
        }
        catch (UserError ex) { SettingsStatus.Text = ex.Message; }
        catch { SettingsStatus.Text = "无法保存配置，请检查本机用户目录的写入权限。"; }
    }
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var cancellation = new CancellationTokenSource();
        testing = cancellation;
        TestButton.IsEnabled = false;
        SettingsStatus.Text = "正在用「随机森林」测试连接…";
        try
        {
            _ = await app.Client.ExplainAsync(ReadSettings(), "随机森林", "", cancellation.Token, stream: false);
            SettingsStatus.Text = "连接成功。点击「保存配置」后即可使用。";
        }
        catch (OperationCanceledException) { SettingsStatus.Text = "连接测试已取消。"; }
        catch (UserError ex) { SettingsStatus.Text = ex.Message; }
        catch { SettingsStatus.Text = "连接测试失败，请检查设置后重试。"; }
        finally { TestButton.IsEnabled = true; testing = null; cancellation.Dispose(); }
    }
}
