using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Batcomputer;

/// <summary>
/// Release-only visual audit for Batcomputer's top-level windows. It captures compact JPEGs and
/// checks that actionable controls remain inside the visible client area after WinForms completes
/// DPI scaling. The output lives outside the portable release and is safe to delete at any time.
/// </summary>
internal static class UiAuditCaptureService
{
    private sealed record AuditFinding(string Window, string Severity, string Message);
    private sealed record CaptureResult(string Name, string File, int Width, int Height, long Bytes);
    private static int _captureMaxWidth = 1280;
    private static int _captureMaxHeight = 820;
    private static long _captureJpegQuality = 38L;
    private static long _contactSheetJpegQuality = 34L;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);

    public static int Run(string outputRoot, bool highQuality = false)
    {
        _captureMaxWidth = highQuality ? 1920 : 1280;
        _captureMaxHeight = highQuality ? 1200 : 820;
        _captureJpegQuality = highQuality ? 92L : 38L;
        _contactSheetJpegQuality = highQuality ? 82L : 34L;
        Directory.CreateDirectory(outputRoot);
        var captures = new List<CaptureResult>();
        var findings = new List<AuditFinding>();
        var capturedFormTypes = new HashSet<Type>();

        try
        {
            CaptureMainSurfaces(outputRoot, captures, findings, capturedFormTypes);

            var settings = AppSettings.BuiltInDefaults();
            CaptureSettingsTabs(outputRoot, settings, captures, findings, capturedFormTypes);

            var sampleProject = new NativeSuitProject
            {
                SlotId = "batman_ui_audit",
                DisplayName = "UI Audit Suit",
                Description = "Checks long descriptions, controls, and dialog layout before release.",
                PawnTag = "Pawns.Playable.Batman.UiAudit",
                ProgressTag = "GameProgress.Story.TheBatman2025",
            };
            var sampleMesh = new CustomStaticMeshImport
            {
                Id = "ui_audit_mesh",
                DisplayName = "Long Custom Cowl Name",
                SourceObjRelativePath = "Meshes\\ui-audit-cowl.obj",
                Target = "Head",
                AttachSocket = "HeadStud_Attach_Socket",
                Scale = 180.5f,
                OffsetX = 1.5f,
                OffsetZ = 5f,
            };
            var sampleTexture = new GeneratedTextureEntry
            {
                DisplayName = "T_UI_Audit_SuitIcon",
                Kind = "Suit selector icon",
                PackagePath = "/Game/Mods/UiAudit/Textures/T_UI_Audit_SuitIcon",
                ObjectPath = "/Game/Mods/UiAudit/Textures/T_UI_Audit_SuitIcon.T_UI_Audit_SuitIcon",
            };
            var sampleMod = new NativeSuitModProject
            {
                ModId = "UiAuditMod",
                DisplayName = "UI Audit Mod",
                Description = "A representative multi-suit release used only by the UI audit.",
                PackageBaseName = "UiAuditMod_P",
                ContentRoot = "/Game/Mods/UiAuditMod",
                StringTablePackage = "/Game/Mods/UiAuditMod/Localization/ST_UiAuditMod.ST_UiAuditMod",
            };
            var projects = new[]
            {
                new SuitProjectService.ProjectSummary("batman_ui_audit", "UI Audit Batman", "C:\\Audit\\batman.native-suit-project.json", DateTime.Now, "", "/Game/Characters/Minifig/Batman/BP_Batman_Playable"),
                new SuitProjectService.ProjectSummary("nightwing_ui_audit", "A Very Long Nightwing Suit Name Used To Check Ellipsis", "C:\\Audit\\nightwing.native-suit-project.json", DateTime.Now.AddDays(-2), "", "/Game/Characters/Minifig/Nightwing/BP_Nightwing_Playable"),
            };

            var cases = new (string Name, Func<Form> Factory, int WaitMs)[]
            {
                ("Dialog - Delete texture", () => Dialog.CreateForm(null, new Dialog.Model
                {
                    WindowTitle = "Batcomputer",
                    Title = "Delete texture",
                    Message = "Delete texture 'WhoLaughsSuit' from this suit and remove its generated output folder?",
                    Severity = Dialog.Level.Crit,
                    PrimaryText = "Delete texture",
                    SecondaryText = "Cancel",
                    Fields = { ("ASSET", "/Game/Mods/WhoLaughs/Textures/T_WhoLaughsSuit") },
                }), 150),
                ("Dialog - Long warning", () => Dialog.CreateForm(null, new Dialog.Model
                {
                    WindowTitle = "Batcomputer - Release warning",
                    Title = "A long warning remains readable",
                    Message = "This long message checks wrapping, scrolling, action placement, and the maximum-height behavior used by release diagnostics.",
                    Severity = Dialog.Level.Warn,
                    PrimaryText = "Continue anyway",
                    SecondaryText = "Go back",
                    CalloutTitle = "Representative diagnostics",
                    CalloutDetail = string.Join("\n", Enumerable.Repeat("A representative diagnostic line with a long generated asset path under /Game/Mods/UiAudit/Textures.", 12)),
                }), 150),
                ("Asset refresh", () => new AssetRefreshProgressForm(), 150),
                ("Asset refresh - first run", () => new AssetRefreshProgressForm(firstRun: true), 150),
                ("Base character picker", () => new BaseCharacterPicker(), 200),
                ("Gameplay donor picker", () => new BaseCharacterPicker(playablesOnly: true), 200),
                ("Manual base wizard", () => new BaseWizard("UI Audit Suit", "UiAudit", "C:\\Audit\\Playable.uasset", "C:\\Audit\\Cutscene.uasset", "C:\\Audit\\DCMD.uasset"), 150),
                ("Custom mesh - import", () => new CustomStaticMeshImportDialog(null, "C:\\Audit\\sample-cowl.obj"), 150),
                ("Custom mesh - edit", () => new CustomStaticMeshImportDialog(sampleMesh, "C:\\Audit\\sample-cowl.obj"), 150),
                ("First-run wizard", () =>
                {
                    var form = new FirstRunWizard(AppSettings.BuiltInDefaults());
                    form.ConfigureForUiAudit();
                    return form;
                }, 150),
                ("Saved suit library", () => new LoadSuitDialog(projects), 150),
                ("Material library", () => new MaterialCatalogPicker(), 250),
                ("Material template picker", () => new MaterialTemplatePicker(new MaterialTemplateCatalogService.Target(
                    "Face", "Face", 0, "/Game/Characters/Attachments/LEGOface/SK_LEGOface")), 250),
                ("Material editor", () => new MaterialWizard("C:\\Audit", "UiAudit", "MI_UI_Audit", new[] { sampleTexture }), 150),
                ("Material editor - face helpers expanded", () =>
                {
                    var form = new MaterialWizard("C:\\Audit", "UiAudit", "MI_UI_Audit_Face", new[] { sampleTexture });
                    form.ConfigureFaceHelpersForUiAudit();
                    return form;
                }, 150),
                ("Mod details", () => new ModDetailsDialog(sampleMod, new[] { ("UI Audit Batman", "batman_ui_audit"), ("UI Audit Nightwing", "nightwing_ui_audit") }, true, "C:\\Audit\\Builds\\UiAuditMod"), 150),
                ("Native identity", () => new NativeIdentityDialog(sampleProject, sampleProject.PawnTag), 150),
                ("Registry writer progress", () => new RegistryWriterProgressForm(), 150),
                ("Suit icons", () => new UimdIconsDialog(
                    "/Game/Characters/Metadata/DA_UIMD_Batman",
                    new NativeMetadataDonorService.Icons("/Game/UI/Menu", "/Game/UI/Suit", "/Game/UI/Left", "/Game/UI/Right"),
                    "", sampleTexture.ObjectPath, "", "", new[] { sampleTexture }), 150),
                ("3D preview window", () => new ModelPreviewForm(
                    "<html><body style='margin:0;background:#191c22;color:#eee;font:700 20px sans-serif;display:grid;place-items:center'><div>3D preview window audit</div></body></html>",
                    "Batcomputer - 3D preview"), 1500),
            };

            foreach (var item in cases)
            {
                CaptureCase(item.Name, item.Factory, item.WaitMs, outputRoot, captures, findings, capturedFormTypes);
            }

            var passed = new ModReleaseValidationService.Result();
            passed.AddWarning("texture", "A representative non-blocking warning with enough text to exercise wrapping.", "batman_ui_audit");
            CaptureCase("Build check - passed", () => ReleasePreflightForm.CreateForUiAudit("UI Audit Mod", passed), 150,
                outputRoot, captures, findings, capturedFormTypes);

            var blocked = new ModReleaseValidationService.Result();
            blocked.AddError("registry", "The native registry writer could not find a representative generated asset.", "batman_ui_audit");
            blocked.AddWarning("texture", "A legacy texture has incomplete recorded dimensions or pixel format.", "batman_ui_audit");
            CaptureCase("Build check - errors", () => ReleasePreflightForm.CreateForUiAudit("UI Audit Mod", blocked), 150,
                outputRoot, captures, findings, capturedFormTypes);

            CaptureProgressDialog(outputRoot, captures, findings, capturedFormTypes);
            RecordUncapturedFormTypes(capturedFormTypes, findings);
            CreateContactSheet(outputRoot, captures);
            WriteReports(outputRoot, captures, findings);
            return findings.Any(f => f.Severity == "ERROR") ? 2 : 0;
        }
        catch (Exception ex)
        {
            findings.Add(new AuditFinding("UI audit", "ERROR", ex.ToString()));
            WriteReports(outputRoot, captures, findings);
            return 1;
        }
    }

    private static void CaptureMainSurfaces(
        string outputRoot,
        List<CaptureResult> captures,
        List<AuditFinding> findings,
        ISet<Type> capturedFormTypes)
    {
        using var form = new MainForm();
        capturedFormTypes.Add(typeof(MainForm));
        ShowAndSettle(form, 500);
        foreach (var surface in MainForm.UiAuditSurfaceNames)
        {
            try
            {
                form.SelectUiAuditSurface(surface);
                Settle(180);
                CaptureVisibleForm(form, $"Main - {surface}", outputRoot, captures, findings);
            }
            catch (Exception ex)
            {
                findings.Add(new AuditFinding($"Main - {surface}", "ERROR", $"Could not render surface: {ex.Message}"));
            }
        }
        form.Close();
        Settle(80);
    }

    private static void CaptureSettingsTabs(
        string outputRoot,
        AppSettings settings,
        List<CaptureResult> captures,
        List<AuditFinding> findings,
        ISet<Type> capturedFormTypes)
    {
        using var form = new SettingsForm(settings, firstRun: false);
        capturedFormTypes.Add(typeof(SettingsForm));
        ShowAndSettle(form, 180);
        foreach (var (name, index) in new[] { ("Paths", 0), ("General", 1), ("Visual", 2) })
        {
            form.SelectUiAuditTab(index);
            Settle(100);
            CaptureVisibleForm(form, $"Settings - {name}", outputRoot, captures, findings);
        }
        form.Close();
        Settle(60);
    }

    private static void CaptureCase(
        string name,
        Func<Form> factory,
        int waitMs,
        string outputRoot,
        List<CaptureResult> captures,
        List<AuditFinding> findings,
        ISet<Type> capturedFormTypes)
    {
        try
        {
            Console.WriteLine($"UI audit: {name} - constructing");
            using var form = factory();
            capturedFormTypes.Add(form.GetType());
            Console.WriteLine($"UI audit: {name} - rendering");
            ShowAndSettle(form, waitMs);
            Console.WriteLine($"UI audit: {name} - capturing");
            CaptureVisibleForm(form, name, outputRoot, captures, findings);
            form.Close();
            Settle(60);
            Console.WriteLine($"UI audit: {name} - complete");
        }
        catch (Exception ex)
        {
            findings.Add(new AuditFinding(name, "ERROR", $"Could not construct or capture window: {ex.Message}"));
            Console.WriteLine($"UI audit: {name} - ERROR {ex.Message}");
        }
    }

    private static void CaptureProgressDialog(
        string outputRoot,
        List<CaptureResult> captures,
        List<AuditFinding> findings,
        ISet<Type> capturedFormTypes)
    {
        using var owner = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(20, 20),
            ClientSize = new Size(240, 100),
            ShowInTaskbar = false,
            Opacity = 0.01,
        };
        owner.Show();
        Settle(40);
        using var progress = new ProgressDialog(owner, "Packaging UI Audit Mod", 4);
        capturedFormTypes.Add(typeof(ProgressDialog));
        progress.Advance(2, "Staging representative assets…");
        Settle(150);
        CaptureVisibleForm(progress, "Progress dialog", outputRoot, captures, findings);
        progress.Close();
        owner.Close();
        Settle(60);
    }

    private static void ShowAndSettle(Form form, int waitMs)
    {
        var working = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(working.Left + 12, working.Top + 12);
        form.Show();
        form.Activate();
        form.PerformLayout();
        PerformLayoutRecursive(form);
        Settle(waitMs);
        form.PerformLayout();
        PerformLayoutRecursive(form);
        Settle(60);
    }

    private static void Settle(int milliseconds)
    {
        var until = Environment.TickCount64 + Math.Max(0, milliseconds);
        do
        {
            Application.DoEvents();
            Thread.Sleep(15);
        } while (Environment.TickCount64 < until);
    }

    private static void PerformLayoutRecursive(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            child.PerformLayout();
            PerformLayoutRecursive(child);
        }
    }

    private static void CaptureVisibleForm(
        Form form,
        string name,
        string outputRoot,
        List<CaptureResult> captures,
        List<AuditFinding> findings)
    {
        ValidateLayout(form, name, findings);
        var fileName = SafeFileName(name) + ".jpg";
        var path = Path.Combine(outputRoot, fileName);
        using var original = CaptureWindow(form);
        using var compact = ResizeToFit(original, _captureMaxWidth, _captureMaxHeight);
        SaveJpeg(compact, path, _captureJpegQuality);
        captures.Add(new CaptureResult(name, fileName, compact.Width, compact.Height, new FileInfo(path).Length));
    }

    private static Bitmap CaptureWindow(Form form)
    {
        if (GetWindowRect(form.Handle, out var rect))
        {
            var width = Math.Max(1, rect.Right - rect.Left);
            var height = Math.Max(1, rect.Bottom - rect.Top);
            var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Theme.WindowBg);
            var hdc = graphics.GetHdc();
            try
            {
                if (PrintWindow(form.Handle, hdc, 0x00000002))
                {
                    return bitmap;
                }
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
            bitmap.Dispose();
        }

        var fallback = new Bitmap(Math.Max(1, form.ClientSize.Width), Math.Max(1, form.ClientSize.Height), PixelFormat.Format24bppRgb);
        form.DrawToBitmap(fallback, new Rectangle(Point.Empty, fallback.Size));
        return fallback;
    }

    private static Bitmap ResizeToFit(Bitmap source, int maxWidth, int maxHeight)
    {
        var scale = Math.Min(1d, Math.Min(maxWidth / (double)source.Width, maxHeight / (double)source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var resized = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(resized);
        graphics.Clear(Theme.WindowBg);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        return resized;
    }

    private static void SaveJpeg(Image image, string path, long quality)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        image.Save(path, codec, parameters);
    }

    private static void ValidateLayout(Form form, string windowName, ICollection<AuditFinding> findings)
    {
        var formClient = new Rectangle(Point.Empty, form.ClientSize);
        var buttons = Descendants(form).OfType<Button>().Where(IsActuallyVisible).ToList();
        foreach (var button in buttons)
        {
            if (HasAutoScrollAncestor(button))
            {
                continue;
            }

            var rect = form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
            if (rect.Width < 1 || rect.Height < 1 || !formClient.IntersectsWith(rect))
            {
                findings.Add(new AuditFinding(windowName, "ERROR", $"Button '{button.Text}' is outside the visible client area ({rect})."));
                continue;
            }

            if (rect.Left < -1 || rect.Top < -1 || rect.Right > formClient.Right + 1 || rect.Bottom > formClient.Bottom + 1)
            {
                findings.Add(new AuditFinding(windowName, "ERROR", $"Button '{button.Text}' is clipped by the window edge ({rect}; client {formClient})."));
            }

            if (!string.IsNullOrWhiteSpace(button.Text))
            {
                var measured = TextRenderer.MeasureText(button.Text, button.Font).Width + 18;
                if (measured > button.ClientSize.Width + 2)
                {
                    findings.Add(new AuditFinding(windowName, "WARN", $"Button text may be clipped: '{button.Text}' needs about {measured}px, has {button.ClientSize.Width}px."));
                }
            }
        }

        ValidateDialogButton(form, form.AcceptButton, "default", windowName, formClient, findings);
        ValidateDialogButton(form, form.CancelButton, "cancel", windowName, formClient, findings);
    }

    private static void ValidateDialogButton(
        Form form,
        IButtonControl? action,
        string role,
        string windowName,
        Rectangle formClient,
        ICollection<AuditFinding> findings)
    {
        if (action is not Control control)
        {
            return;
        }

        var rect = form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
        if (!IsActuallyVisible(control) || !formClient.IntersectsWith(rect))
        {
            findings.Add(new AuditFinding(windowName, "ERROR", $"The {role} action '{control.Text}' is not visible."));
        }
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    private static bool IsActuallyVisible(Control control)
    {
        for (Control? current = control; current is not null; current = current.Parent)
        {
            if (!current.Visible)
            {
                return false;
            }
        }
        return true;
    }

    private static bool HasAutoScrollAncestor(Control control)
    {
        for (Control? current = control.Parent; current is not null && current is not Form; current = current.Parent)
        {
            if (current is ScrollableControl scrollable && scrollable.AutoScroll)
            {
                return true;
            }
        }
        return false;
    }

    private static void RecordUncapturedFormTypes(ISet<Type> capturedFormTypes, ICollection<AuditFinding> findings)
    {
        var publicForms = typeof(MainForm).Assembly.GetTypes()
            .Where(type => type.IsPublic && !type.IsAbstract && typeof(Form).IsAssignableFrom(type))
            .OrderBy(type => type.Name)
            .ToList();
        foreach (var type in publicForms.Where(type => !capturedFormTypes.Contains(type)))
        {
            findings.Add(new AuditFinding(type.Name, "WARN", "Top-level Form type was not captured by the gallery."));
        }
    }

    private static void CreateContactSheet(string outputRoot, IReadOnlyList<CaptureResult> captures)
    {
        if (captures.Count == 0)
        {
            return;
        }

        const int cellWidth = 330;
        const int cellHeight = 230;
        const int columns = 3;
        var rows = (captures.Count + columns - 1) / columns;
        using var sheet = new Bitmap(cellWidth * columns, cellHeight * rows, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(sheet);
        graphics.Clear(Theme.WindowBg);
        using var labelFont = new Font(Theme.Body.FontFamily, 9f, FontStyle.Bold);
        using var labelBrush = new SolidBrush(Color.WhiteSmoke);

        for (var index = 0; index < captures.Count; index++)
        {
            var capture = captures[index];
            using var image = Image.FromFile(Path.Combine(outputRoot, capture.File));
            var cellX = index % columns * cellWidth;
            var cellY = index / columns * cellHeight;
            var area = new Rectangle(cellX + 6, cellY + 26, cellWidth - 12, cellHeight - 34);
            var scale = Math.Min(area.Width / (double)image.Width, area.Height / (double)image.Height);
            var width = Math.Max(1, (int)(image.Width * scale));
            var height = Math.Max(1, (int)(image.Height * scale));
            var target = new Rectangle(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height);
            graphics.DrawString(capture.Name, labelFont, labelBrush, cellX + 7, cellY + 5);
            graphics.DrawImage(image, target);
        }

        SaveJpeg(sheet, Path.Combine(outputRoot, "00-contact-sheet.jpg"), _contactSheetJpegQuality);
    }

    private static void WriteReports(string outputRoot, IReadOnlyList<CaptureResult> captures, IReadOnlyList<AuditFinding> findings)
    {
        var jsonPath = Path.Combine(outputRoot, "ui-audit-report.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(new
        {
            generatedUtc = DateTime.UtcNow,
            captureCount = captures.Count,
            captures,
            findings,
        }, new JsonSerializerOptions { WriteIndented = true }));

        var report = new StringBuilder();
        report.AppendLine("# Batcomputer UI audit");
        report.AppendLine();
        report.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"Captured windows/surfaces: {captures.Count}");
        report.AppendLine($"Errors: {findings.Count(f => f.Severity == "ERROR")}");
        report.AppendLine($"Warnings: {findings.Count(f => f.Severity == "WARN")}");
        report.AppendLine();
        report.AppendLine("## Findings");
        report.AppendLine();
        if (findings.Count == 0)
        {
            report.AppendLine("No automated bounds issues were found.");
        }
        else
        {
            foreach (var finding in findings)
            {
                report.AppendLine($"- **{finding.Severity} — {finding.Window}:** {finding.Message}");
            }
        }
        report.AppendLine();
        report.AppendLine("## Captures");
        report.AppendLine();
        foreach (var capture in captures)
        {
            report.AppendLine($"- [{capture.Name}]({capture.File}) — {capture.Width}×{capture.Height}, {capture.Bytes / 1024d:N0} KB");
        }
        File.WriteAllText(Path.Combine(outputRoot, "ui-audit-report.md"), report.ToString());
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray());
        return cleaned.Replace("  ", " ").Trim();
    }
}
