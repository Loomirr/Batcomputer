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
            CaptureThemePreviews(outputRoot, captures, findings, capturedFormTypes);

            var sampleProject = new NativeSuitProject
            {
                SlotId = "batman_ui_audit",
                DisplayName = "UI Audit Suit",
                Description = "Checks long descriptions, controls, and dialog layout before release.",
                PawnTag = "Pawns.Playable.Batman.UiAudit",
                ProgressTag = "GameProgress.Story.TheBatman2025",
                UseCustomArchetype = true,
                LocomotionOverrides =
                [
                    new AnimSequenceOverride
                    {
                        DonorSequence = "A_Idle_Batman",
                        DonorSequencePackage = "/Game/Animation/LEGOfig/Batman/Movement/A_Idle_Batman",
                        ReplacementSequence = "A_Idle_UiAudit",
                        ReplacementPackage = "/Game/Mods/UiAudit/Animations/A_Idle_UiAudit",
                    },
                ],
            };
            var sampleAnimation = new AnimLibraryEntry
            {
                Id = "ui-audit-animation",
                Name = "UI Audit custom idle",
                SourceMode = "preserve-path",
                PackagePath = "/Game/Mods/UiAudit/Animations/A_Idle_UiAudit",
                AssetClass = "/Script/Engine.AnimSequence",
                Skeleton = "/Game/Mods/UiAudit/Animations/Rig/SKEL_UiAudit",
                HealthStatus = "healthy",
                IsAvailable = true,
                Inspected = true,
                CachedFiles = ["Cache/ui-audit-animation/A_Idle_UiAudit.uasset"],
                Dependencies = ["/Game/Animation/Shared/Curves/Curve_LEGOfig"],
                SupportPackages =
                [
                    new AnimLibraryCachedPackage
                    {
                        PackagePath = "/Game/Mods/UiAudit/Animations/Rig/SKEL_UiAudit",
                        AssetClass = "Skeleton",
                        Inspected = true,
                        Dependencies = ["/Game/Mods/UiAudit/Animations/Rig/PHYS_UiAudit"],
                    },
                    new AnimLibraryCachedPackage
                    {
                        PackagePath = "/Game/Mods/UiAudit/Animations/Rig/PHYS_UiAudit",
                        AssetClass = "PhysicsAsset",
                        Inspected = true,
                    },
                ],
            };
            var sampleAnimationLibrary = new AnimLibrary { Entries = [sampleAnimation] };
            var sampleAnimationTarget = new CharacterAnimationTargetSnapshot(
                "ui-audit-locomotion-target",
                CharacterAnimationReferenceKind.LocomotionSequence,
                "/Game/Animation/LEGOfig/Batman/Movement/ABP_Core_Batman",
                "AnimBlueprintGeneratedClass",
                "/Game/Animation/LEGOfig/Batman/Movement/A_Idle_Batman",
                "/Game/Mods/UiAudit/Animations/A_Idle_UiAudit",
                "A_Idle_Batman",
                "A_Idle_UiAudit",
                "AnimSequence",
                "AnimSequence",
                -1,
                -1,
                -1,
                0,
                true,
                "sequence");
            var sampleMontageTarget = new CharacterAnimationTargetSnapshot(
                "ui-audit-jump-target",
                CharacterAnimationReferenceKind.AnimFile,
                "/Game/Animation/MontageAnimSets/Traversal/MAS_Movement_Batman",
                "TTAnimSet",
                "/Game/Animation/LEGOfig/Batman/Movement/AM_Jump_Batman",
                "/Game/Animation/LEGOfig/Batman/Movement/AM_Jump_Batman",
                "AM_Jump_Batman",
                "AM_Jump_Batman",
                "AnimMontage",
                "AnimMontage",
                0,
                0,
                -1,
                1,
                false,
                "");
            var sampleAnimationGraph = new CharacterAnimationSnapshot(
                sampleProject.SlotId,
                sampleProject.DisplayName,
                "Batman",
                "/Game/Animation/MontageAnimSets/Character/MAS_Char_Batman",
                "/Game/Animation/LayerAnimSets/Character/LAS_Char_Batman",
                [
                    new CharacterAnimationSetSnapshot(
                        "ui-audit-movement-set",
                        0,
                        CharacterAnimationSetKind.Montage,
                        "Movement",
                        "/Game/Animation/MontageAnimSets/Traversal/MAS_Movement_Batman",
                        "/Game/Animation/MontageAnimSets/Traversal/MAS_Movement_Batman",
                        false,
                        "",
                        [
                            new CharacterAnimationSlotSnapshot(
                                "ui-audit-jump-slot",
                                "/Game/Animation/MontageAnimSets/Traversal/MAS_Movement_Batman",
                                CharacterAnimationSetKind.Montage,
                                0,
                                "Animation.Action.Jump",
                                ["Animation.Status.Moving"],
                                1,
                                [sampleMontageTarget]),
                        ]),
                ],
                [sampleAnimationTarget],
                []);
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
            var samplePart = new NativeSuitPartRecord
            {
                Slot = "Hair",
                Context = "playable",
                SemanticKind = "Hair",
                IsKnownVisualSlot = true,
                IsLikelyGraftCandidate = true,
                SourcePackagePath = "/Game/Characters/Minifig/UiAudit/BP_UiAudit_Playable",
                MeshKind = "StaticMesh",
                MeshObjectName = "SM_UIAuditHair",
                MeshPackagePath = "/Game/Characters/Attachments/Hair/UiAudit/SM_UIAuditHair",
                MeshObjectPath = "/Game/Characters/Attachments/Hair/UiAudit/SM_UIAuditHair.SM_UIAuditHair",
                ComponentClass = "StaticMeshComponent",
                ComponentTemplateExport = "Head_GEN_VARIABLE",
                ParentComponentOrVariableName = "CharacterMesh0",
                AttachSocket = "HeadStud_Attach_Socket",
                Materials =
                [
                    new NativeSuitObjectRef
                    {
                        ObjectName = "MI_UIAuditHair",
                        PackagePath = "/Game/Characters/Attachments/Hair/UiAudit/MI_UIAuditHair",
                        ObjectPath = "/Game/Characters/Attachments/Hair/UiAudit/MI_UIAuditHair.MI_UIAuditHair",
                        ClassName = "MaterialInstanceConstant",
                    },
                ],
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
                ("Animation import progress", () => new AnimationImportProgressForm("UiAuditAnimations_P"), 150),
                ("Animation explorer", () => new AnimationExplorerForm(
                    sampleProject,
                    sampleAnimationLibrary,
                    sampleAnimation.PackagePath,
                    sampleAnimationGraph), 200),
                ("Animation replacement picker", () => new AnimationReplacementPickerForm(
                    sampleAnimationTarget,
                    sampleAnimationLibrary), 200),
                ("Base character picker", () => new BaseCharacterPicker(), 800),
                ("Gameplay donor picker", () => new BaseCharacterPicker(playablesOnly: true), 400),
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
                ("Native part inspector", () =>
                {
                    var form = new PartInspectorForm(samplePart);
                    form.ConfigureForUiAudit();
                    return form;
                }, 250),
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
                ValidateResizeBehavior(form, $"Main - {surface}", findings);
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
            ValidateResizeBehavior(form, $"Settings - {name}", findings);
        }
        form.Close();
        Settle(60);
    }

    private static void CaptureThemePreviews(
        string outputRoot,
        List<CaptureResult> captures,
        List<AuditFinding> findings,
        ISet<Type> capturedFormTypes)
    {
        var previousSettings = AppSettings.Current;
        try
        {
            foreach (var visualTheme in Theme.VisualThemes)
            {
                var settings = AppSettings.BuiltInDefaults();
                settings.VisualTheme = visualTheme.Name;
                AppSettings.Current = settings;

                using (var main = new MainForm())
                {
                    capturedFormTypes.Add(typeof(MainForm));
                    ShowAndSettle(main, 220);
                    main.SelectUiAuditSurface("Home - Mods");
                    Settle(120);
                    CaptureVisibleForm(
                        main,
                        $"Theme - {visualTheme.Name} - Home",
                        outputRoot,
                        captures,
                        findings);
                    ValidateResizeBehavior(main, $"Theme - {visualTheme.Name} - Home", findings);
                    main.Close();
                    Settle(50);
                }

                using var visual = new SettingsForm(settings, firstRun: false);
                capturedFormTypes.Add(typeof(SettingsForm));
                ShowAndSettle(visual, 120);
                visual.SelectUiAuditTab(2);
                Settle(80);
                CaptureVisibleForm(
                    visual,
                    $"Theme - {visualTheme.Name} - Settings",
                    outputRoot,
                    captures,
                    findings);
                ValidateResizeBehavior(visual, $"Theme - {visualTheme.Name} - Settings", findings);
                visual.Close();
                Settle(40);
            }
        }
        finally
        {
            AppSettings.Current = previousSettings;
        }
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
            ValidateResizeBehavior(form, name, findings);
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
        using var owner = new AdaptiveDialogForm
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(20, 20),
            ClientSize = new Size(240, 100),
            ShowInTaskbar = false,
            Opacity = 0.01,
        };
        AdaptiveWindowManager.Prepare(owner);
        owner.Show();
        AdaptiveWindowManager.Configure(owner);
        Settle(40);
        using var progress = new ProgressDialog(owner, "Packaging UI Audit Mod", 4);
        AdaptiveWindowManager.Configure(progress);
        capturedFormTypes.Add(typeof(ProgressDialog));
        progress.Advance(2, "Staging representative assets…");
        Settle(150);
        CaptureVisibleForm(progress, "Progress dialog", outputRoot, captures, findings);
        ValidateResizeBehavior(progress, "Progress dialog", findings);
        progress.Close();
        owner.Close();
        Settle(60);
    }

    private static void ShowAndSettle(Form form, int waitMs)
    {
        var working = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(working.Left + 12, working.Top + 12);
        // Match AdaptiveForm's two-phase setup: chrome before handle creation, then monitor/DPI
        // fitting after WinForms has scaled the controls.
        AdaptiveWindowManager.Prepare(form);
        form.Show();
        AdaptiveWindowManager.Configure(form);
        form.Activate();
        form.PerformLayout();
        PerformLayoutRecursive(form);
        Settle(waitMs);
        form.PerformLayout();
        PerformLayoutRecursive(form);
        Settle(60);
        form.Invalidate(true);
        form.Update();
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
        if (!AdaptiveWindowManager.IsResizableBorderForTest(form.FormBorderStyle))
        {
            findings.Add(new AuditFinding(windowName, "ERROR", $"Window border is not resizable ({form.FormBorderStyle})."));
        }
        if (form.ControlBox && !form.MaximizeBox)
        {
            findings.Add(new AuditFinding(windowName, "ERROR", "Resizable window does not expose a maximize action."));
        }

        var formClient = new Rectangle(Point.Empty, form.ClientSize);
        var buttons = Descendants(form).OfType<Button>().Where(IsActuallyVisible).ToList();
        foreach (var button in buttons)
        {
            if (ScrollableAncestors(button).Count > 0)
            {
                ValidateScrollReachability(button, windowName, findings);
            }
            else
            {
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
            }

            if (!string.IsNullOrWhiteSpace(button.Text))
            {
                var measured = TextRenderer.MeasureText(button.Text, button.Font).Width + 18;
                if (measured > button.ClientSize.Width + 2)
                {
                    findings.Add(new AuditFinding(windowName, "WARN", $"Button text may be clipped: '{button.Text}' needs about {measured}px, has {button.ClientSize.Width}px."));
                }
            }
            else if (!string.IsNullOrWhiteSpace(button.AccessibleName))
            {
                ValidatePaintedTileText(button, windowName, findings);
            }
        }

        foreach (var label in Descendants(form).OfType<Label>().Where(IsActuallyVisible))
        {
            ValidateWrappedLabel(label, windowName, findings);
        }

        ValidateDialogButton(form, form.AcceptButton, "default", windowName, formClient, findings);
        ValidateDialogButton(form, form.CancelButton, "cancel", windowName, formClient, findings);
    }

    private static void ValidatePaintedTileText(
        Button button,
        string windowName,
        ICollection<AuditFinding> findings)
    {
        var title = button.AccessibleName ?? "";
        var subtitle = button.AccessibleDescription ?? "";
        var dpi = Math.Max(96, button.DeviceDpi);
        int Scale(int logical) => Math.Max(1, logical * dpi / 96);
        var horizontalPadding = Scale(8);
        var verticalPadding = Scale(8);
        var gap = string.IsNullOrWhiteSpace(subtitle) ? 0 : Scale(4);
        var textWidth = Math.Max(1, button.ClientSize.Width - horizontalPadding * 2);
        const TextFormatFlags flags =
            TextFormatFlags.WordBreak |
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.NoPrefix |
            TextFormatFlags.NoPadding;

        var titleHeight = TextRenderer.MeasureText(
            title,
            Theme.BodyStrong,
            new Size(textWidth, int.MaxValue),
            flags).Height;
        var subtitleHeight = string.IsNullOrWhiteSpace(subtitle)
            ? 0
            : TextRenderer.MeasureText(
                subtitle,
                Theme.Caption,
                new Size(textWidth, int.MaxValue),
                flags).Height;
        var requiredHeight = verticalPadding * 2 + titleHeight + gap + subtitleHeight;
        if (requiredHeight > button.ClientSize.Height + 2)
        {
            findings.Add(new AuditFinding(
                windowName,
                "ERROR",
                $"Tile '{CompactAuditText(title)}' needs {requiredHeight}px for its title and subtitle but has {button.ClientSize.Height}px."));
        }
    }

    private static void ValidateWrappedLabel(
        Label label,
        string windowName,
        ICollection<AuditFinding> findings)
    {
        if (label.AutoSize || label.AutoEllipsis || string.IsNullOrWhiteSpace(label.Text) ||
            label.ClientSize.Width <= label.Padding.Horizontal ||
            label.ClientSize.Height < label.Font.Height * 2)
        {
            return;
        }

        var available = new Size(
            Math.Max(1, label.ClientSize.Width - label.Padding.Horizontal),
            Math.Max(1, label.ClientSize.Height - label.Padding.Vertical));
        var measured = TextRenderer.MeasureText(
            label.Text,
            label.Font,
            new Size(available.Width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        if (measured.Height > available.Height + 2)
        {
            findings.Add(new AuditFinding(windowName, "ERROR",
                $"Label '{CompactAuditText(label.Text)}' needs {measured.Height}px but has {available.Height}px."));
        }
    }

    private static string CompactAuditText(string value)
    {
        var compact = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 64 ? compact : compact[..61] + "...";
    }

    private static void ValidateResizeBehavior(Form form, string windowName, ICollection<AuditFinding> findings)
    {
        if (form.WindowState != FormWindowState.Normal)
        {
            return;
        }

        var original = form.Size;
        var minimum = new Size(
            Math.Max(1, form.MinimumSize.Width),
            Math.Max(1, form.MinimumSize.Height));
        var working = Screen.FromControl(form).WorkingArea;
        var maximum = new Size(
            Math.Max(1, working.Width - 24),
            Math.Max(1, working.Height - 24));
        var candidates = new[]
        {
            ("minimum", minimum),
            ("compact", new Size(
                Math.Max(minimum.Width, original.Width - 140),
                Math.Max(minimum.Height, original.Height - 90))),
            ("expanded", new Size(
                Math.Min(maximum.Width, original.Width + 140),
                Math.Min(maximum.Height, original.Height + 90))),
        };

        try
        {
            foreach (var (label, requested) in candidates.Where(candidate => candidate.Item2 != original).Distinct())
            {
                form.Size = requested;
                form.PerformLayout();
                PerformLayoutRecursive(form);
                Settle(30);
                if (Math.Abs(form.Size.Width - requested.Width) > 2 ||
                    Math.Abs(form.Size.Height - requested.Height) > 2)
                {
                    findings.Add(new AuditFinding(windowName, "ERROR",
                        $"Window did not accept its {label} resize (requested {requested}, got {form.Size})."));
                    continue;
                }
                ValidateLayout(form, $"{windowName} ({label})", findings);
            }
        }
        finally
        {
            form.Size = original;
            form.PerformLayout();
            PerformLayoutRecursive(form);
            Settle(30);
        }
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

        if (!IsActuallyVisible(control))
        {
            findings.Add(new AuditFinding(windowName, "ERROR", $"The {role} action '{control.Text}' is not visible."));
            return;
        }

        if (ScrollableAncestors(control).Count > 0)
        {
            return;
        }

        var rect = form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
        if (!formClient.IntersectsWith(rect))
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

    private static IReadOnlyList<ScrollableControl> ScrollableAncestors(Control control)
    {
        var ancestors = new List<ScrollableControl>();
        for (Control? current = control.Parent; current is not null; current = current.Parent)
        {
            if (current is ScrollableControl scrollable && scrollable.AutoScroll)
            {
                ancestors.Add(scrollable);
            }
        }
        return ancestors;
    }

    private static void ValidateScrollReachability(
        Control control,
        string windowName,
        ICollection<AuditFinding> findings)
    {
        var ancestors = ScrollableAncestors(control);
        if (ancestors.Count == 0)
        {
            return;
        }

        var initialBounds = control.RectangleToScreen(control.ClientRectangle);
        if (ancestors.All(scrollable =>
                scrollable.RectangleToScreen(scrollable.ClientRectangle).IntersectsWith(initialBounds)))
        {
            return;
        }

        var positions = ancestors.Select(scrollable => scrollable.AutoScrollPosition).ToArray();
        try
        {
            foreach (var scrollable in ancestors)
            {
                scrollable.ScrollControlIntoView(control);
                scrollable.PerformLayout();
            }
            Application.DoEvents();

            var controlBounds = control.RectangleToScreen(control.ClientRectangle);
            foreach (var scrollable in ancestors)
            {
                var viewport = scrollable.RectangleToScreen(scrollable.ClientRectangle);
                if (!viewport.IntersectsWith(controlBounds))
                {
                    findings.Add(new AuditFinding(windowName, "ERROR",
                        $"Button '{control.Text}' cannot be reached through {scrollable.GetType().Name} scrolling."));
                    break;
                }
            }
        }
        finally
        {
            for (var index = ancestors.Count - 1; index >= 0; index--)
            {
                ancestors[index].AutoScrollPosition = new Point(-positions[index].X, -positions[index].Y);
            }
        }
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
