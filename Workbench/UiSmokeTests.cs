using System.Drawing.Imaging;
using System.Text;

namespace DbCheck.Workbench;

internal static class UiSmokeTests
{
    public static int Run(string outputDirectory)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new InvalidOperationException("UI smoke tests must run on an STA thread.");
        Directory.CreateDirectory(outputDirectory);
        using var form = new WorkbenchForm(demo: true);
        form.StartPosition = FormStartPosition.Manual; form.Location = Point.Empty;
        form.Show(); Application.DoEvents();
        var manifest = new StringBuilder("Synthetic UI captures only. No database connection or production metadata.\r\n");
        var count = 0;
        foreach (var size in new[] { new Size(1440, 900), new Size(1000, 720) })
        {
            form.ClientSize = size;
            foreach (var page in new[] { "environment", "scope", "differences", "plan", "data", "history" })
            {
                form.PrepareDemoPage(page); form.PerformLayout(); Application.DoEvents();
                foreach (var control in Descendants(form).Where(c => c.Visible))
                    if (control.Width < 0 || control.Height < 0) throw new InvalidOperationException("Invalid control bounds: " + control.GetType().Name);
                var name = $"{count + 1:00}-{page}-{size.Width}x{size.Height}.png";
                Capture(form, Path.Combine(outputDirectory, name)); manifest.AppendLine(name); count++;
            }
            form.PrepareDemoPage("differences"); Application.DoEvents();
            var viewer = Descendants(form).OfType<GitDiffView>().First(v => v.Visible);
            var selector = Descendants(viewer).OfType<ToolStrip>().SelectMany(s => s.Items.OfType<ToolStripComboBox>()).First();
            selector.SelectedIndex = 1; Application.DoEvents();
            var unifiedName = $"{count + 1:00}-unified-{size.Width}x{size.Height}.png";
            Capture(form, Path.Combine(outputDirectory, unifiedName)); manifest.AppendLine(unifiedName); count++;
            selector.SelectedIndex = 0;
        }
        File.WriteAllText(Path.Combine(outputDirectory, "README.txt"), manifest.ToString());
        form.Close(); Application.DoEvents();
        Console.WriteLine($"UI smoke test passed: {count} screenshots; six pages, split/unified diff, two viewport sizes. No database connection.");
        return 0;
    }

    private static void Capture(Form form, string path)
    {
        using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        var panel = form.Controls[0];
        panel.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(path, ImageFormat.Png);
    }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
