using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace BlackwaterExchange.App;

/// <summary>
/// The icon in the notification area: click to open the window, right-click for the menu. Windows shows its
/// balloon messages as notifications.
/// </summary>
public sealed class Tray : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly IntPtr iconHandle;
    private readonly Engine engine;

    public Tray(Engine engine, Action open, Action quit)
    {
        this.engine = engine;
        var size = Forms.SystemInformation.SmallIconSize.Width;
        var logo = Render((ImageSource)Application.Current.FindResource("Logo"), size);
        iconHandle = logo.GetHicon();
        logo.Dispose();

        var menu = new Forms.ContextMenuStrip();
        var openItem = menu.Items.Add("Open Blackwater Exchange", null, (_, _) => open());
        openItem.Font = new System.Drawing.Font(openItem.Font, System.Drawing.FontStyle.Bold);
        var uploadItem = menu.Items.Add("Upload my ledger now", null, (_, _) => _ = engine.UploadNowAsync(CancellationToken.None));
        menu.Items.Add("Update prices now", null, (_, _) => _ = engine.UpdatePricesNowAsync(CancellationToken.None));
        menu.Items.Add("Open my ledger on the website", null, (_, _) =>
            MainWindow.OpenLedger(engine.Settings));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => quit());
        menu.Opening += (_, _) => uploadItem.Enabled = engine.Status.Linked && engine.Settings.UploadLedger;

        icon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.FromHandle(iconHandle),
            ContextMenuStrip = menu,
            Text = "Blackwater Exchange",
            Visible = true,
        };
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                open();
        };
        engine.StatusChanged += status => Application.Current.Dispatcher.BeginInvoke(() => UpdateText(status));
        UpdateText(engine.Status);
    }

    private void UpdateText(EngineStatus status)
    {
        var text = status.Problem is not null ? "Blackwater Exchange: needs your attention"
            : status.Linked ? $"Blackwater Exchange: linked to {status.LinkedUser}"
            : "Blackwater Exchange: not linked";
        // Windows allows 127 characters.
        icon.Text = text.Length > 127 ? text[..127] : text;
    }

    public void Notify(string title, string text) => icon.ShowBalloonTip(6000, title, text, Forms.ToolTipIcon.None);

    /// <summary>Draws the logo at an icon size.</summary>
    internal static System.Drawing.Bitmap Render(ImageSource image, int size)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawImage(image, new Rect(0, 0, size, size));
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        return new System.Drawing.Bitmap(stream);
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        DestroyIcon(iconHandle);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
