using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DKEzLogoMaker.Models;
using DKEzLogoMaker.Services;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace DKEzLogoMaker;

public partial class MainWindow : Window
{
    private const double MinPreviewZoom = 0.05;
    private const double MaxPreviewZoom = 16.0;

    private readonly SettingsService _settingsService = new();
    private readonly ProjectService _projectService = new();
    private readonly LogoRenderer _renderer = new();
    private readonly DispatcherTimer _settingsSaveTimer;

    private AppSettings _settings;
    private LogoProject _project = LogoProject.CreateDefault();
    private string? _currentProjectPath;
    private bool _isDirty;
    private bool _suppressChanges;
    private double _previewZoom = 1.0;
    private bool _fitPreview;

    private SelectedElement _selectedElement = SelectedElement.None;
    private InteractionMode _interactionMode = InteractionMode.None;
    private Point _pointerStart;
    private double _startCenterX;
    private double _startCenterY;
    private double _startTextSize;
    private double _startImageScaleX;
    private double _startImageScaleY;
    private Rect _startBounds;
    private ResizeHandle _resizeHandle = ResizeHandle.BottomRight;

    public ICommand NewCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ExportCommand { get; }

    public MainWindow()
    {
        InitializeComponent();

        NewCommand = new RelayCommand(_ => NewProject());
        OpenCommand = new RelayCommand(_ => OpenProject());
        SaveCommand = new RelayCommand(_ => SaveProject(false));
        ExportCommand = new RelayCommand(_ => ExportPng());
        DataContext = this;

        InputBindings.Add(new KeyBinding(NewCommand, new KeyGesture(Key.N, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(OpenCommand, new KeyGesture(Key.O, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(SaveCommand, new KeyGesture(Key.S, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(ExportCommand, new KeyGesture(Key.E, ModifierKeys.Control)));

        _settingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _settingsSaveTimer.Tick += (_, _) =>
        {
            _settingsSaveTimer.Stop();
            SaveAppSettings();
        };

        LoadFonts();
        _settings = _settingsService.Load();
        RestoreWindowSettings();
        RestoreEditorState();
        SetPreviewZoom(_settings.PreviewZoom, false);
    }

    private void LoadFonts()
    {
        FontFamilyCombo.ItemsSource = Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void RestoreWindowSettings()
    {
        Width = Math.Max(MinWidth, _settings.WindowWidth);
        Height = Math.Max(MinHeight, _settings.WindowHeight);

        if (!double.IsNaN(_settings.WindowLeft) && !double.IsNaN(_settings.WindowTop))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _settings.WindowLeft;
            Top = _settings.WindowTop;
        }
    }

    private void RestoreEditorState()
    {
        _project = _settings.EditorState ?? LogoProject.CreateDefault();
        _project.UpgradeIfNeeded();

        if (!string.IsNullOrWhiteSpace(_settings.LastImagePath) && File.Exists(_settings.LastImagePath))
        {
            try
            {
                _project.Image.RuntimeBytes = File.ReadAllBytes(_settings.LastImagePath);
                _project.Image.RuntimeSourcePath = _settings.LastImagePath;
                _project.Image.SourceFileName = Path.GetFileName(_settings.LastImagePath);
            }
            catch
            {
                _project.Image.RuntimeBytes = null;
                _project.Image.RuntimeSourcePath = null;
            }
        }

        ApplyProjectToUi(_project);
        _isDirty = false;
        _currentProjectPath = null;
        _selectedElement = SelectedElement.None;
        UpdateTitle();
        RenderPreview();
    }

    private void ApplyProjectToUi(LogoProject project)
    {
        project.UpgradeIfNeeded();
        ConstrainProjectToCanvas(project);

        _suppressChanges = true;
        try
        {
            CanvasWidthBox.Text = project.Canvas.Width.ToString(CultureInfo.InvariantCulture);
            CanvasHeightBox.Text = project.Canvas.Height.ToString(CultureInfo.InvariantCulture);
            SelectComboByTag(BackgroundModeCombo, project.Background.Mode.ToString());
            BackgroundColor1Box.Text = project.Background.Color1;
            BackgroundColor2Box.Text = project.Background.Color2;
            SelectComboByTag(GradientDirectionCombo, project.Background.Direction.ToString());

            TextValueBox.Text = project.Text.Value;
            FontFamilyCombo.Text = project.Text.FontFamily;
            TextSizeBox.Text = FormatNumber(project.Text.Size);
            SelectComboByTag(FontWeightCombo, project.Text.Weight);
            TextSpacingBox.Text = FormatNumber(project.Text.Spacing);
            TextColorBox.Text = project.Text.Color;
            TextCenterXBox.Text = FormatNumber(project.Text.CenterX);
            TextCenterYBox.Text = FormatNumber(project.Text.CenterY);

            ImageScaleXBox.Text = FormatNumber(project.Image.ScaleXPercent);
            ImageScaleYBox.Text = FormatNumber(project.Image.ScaleYPercent);
            ImagePreserveAspectCheckBox.IsChecked = project.Image.PreserveAspectRatio;
            ImageCenterXBox.Text = FormatNumber(project.Image.CenterX);
            ImageCenterYBox.Text = FormatNumber(project.Image.CenterY);
            ImageFileNameText.Text = project.Image.SourceFileName ?? "이미지 없음";
            UpdateImageAspectUi();

            UpdateBackgroundPanels();
            UpdateColorButton(BackgroundColor1Button, project.Background.Color1);
            UpdateColorButton(BackgroundColor2Button, project.Background.Color2);
            UpdateColorButton(TextColorButton, project.Text.Color);
        }
        finally
        {
            _suppressChanges = false;
        }
    }

    private LogoProject ReadProjectFromUi(bool constrainToCanvas = true)
    {
        var width = ParseInt(CanvasWidthBox.Text, _project.Canvas.Width, 1, 8192);
        var height = ParseInt(CanvasHeightBox.Text, _project.Canvas.Height, 1, 8192);

        var project = new LogoProject
        {
            Version = LogoProject.CurrentVersion,
            Canvas = new CanvasSettings
            {
                Width = width,
                Height = height
            },
            Background = new BackgroundSettings
            {
                Mode = ParseEnumTag(BackgroundModeCombo, _project.Background.Mode),
                Color1 = NormalizeColorText(BackgroundColor1Box.Text, _project.Background.Color1),
                Color2 = NormalizeColorText(BackgroundColor2Box.Text, _project.Background.Color2),
                Direction = ParseEnumTag(GradientDirectionCombo, _project.Background.Direction)
            },
            Text = new TextSettings
            {
                Value = TextValueBox.Text ?? string.Empty,
                FontFamily = string.IsNullOrWhiteSpace(FontFamilyCombo.Text) ? "Segoe UI" : FontFamilyCombo.Text.Trim(),
                Size = ParseDouble(TextSizeBox.Text, _project.Text.Size, 0.1, 2048),
                Weight = GetSelectedTag(FontWeightCombo) ?? _project.Text.Weight,
                Spacing = ParseDouble(TextSpacingBox.Text, _project.Text.Spacing, -200, 1000),
                Color = NormalizeColorText(TextColorBox.Text, _project.Text.Color),
                CenterX = ParseDouble(TextCenterXBox.Text, _project.Text.CenterX, -100000, 100000),
                CenterY = ParseDouble(TextCenterYBox.Text, _project.Text.CenterY, -100000, 100000)
            },
            Image = CreateImageSettingsFromUi()
        };

        if (constrainToCanvas)
            ConstrainProjectToCanvas(project);
        return project;
    }

    private ImageSettings CreateImageSettingsFromUi()
    {
        var preserve = ImagePreserveAspectCheckBox.IsChecked == true;
        var scaleX = ParseDouble(ImageScaleXBox.Text, _project.Image.ScaleXPercent, 0.001, 5000);
        var scaleY = preserve
            ? scaleX
            : ParseDouble(ImageScaleYBox.Text, _project.Image.ScaleYPercent, 0.001, 5000);

        return new ImageSettings
        {
            SourceFileName = _project.Image.SourceFileName,
            EmbeddedAssetName = _project.Image.EmbeddedAssetName,
            ScalePercent = scaleX,
            ScaleXPercent = scaleX,
            ScaleYPercent = scaleY,
            PreserveAspectRatio = preserve,
            CenterX = ParseDouble(ImageCenterXBox.Text, _project.Image.CenterX, -100000, 100000),
            CenterY = ParseDouble(ImageCenterYBox.Text, _project.Image.CenterY, -100000, 100000),
            RuntimeSourcePath = _project.Image.RuntimeSourcePath,
            RuntimeBytes = _project.Image.RuntimeBytes
        };
    }

    private void EditorControl_Changed(object sender, EventArgs e)
    {
        if (_suppressChanges || !IsLoaded)
            return;

        var editingCanvasSize = ReferenceEquals(sender, CanvasWidthBox) || ReferenceEquals(sender, CanvasHeightBox);
        _project = ReadProjectFromUi(!editingCanvasSize);
        if (!editingCanvasSize)
            SyncGeometryControlsToProject();
        UpdateImageAspectUi();
        _isDirty = true;
        UpdateBackgroundPanels();
        UpdateColorButton(BackgroundColor1Button, _project.Background.Color1);
        UpdateColorButton(BackgroundColor2Button, _project.Background.Color2);
        UpdateColorButton(TextColorButton, _project.Text.Color);
        UpdateTitle();
        RenderPreview();
        ScheduleSettingsSave();
    }

    private void CanvasSizeBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_suppressChanges || !IsLoaded)
            return;

        _project = ReadProjectFromUi(true);
        SyncGeometryControlsToProject();
        _isDirty = true;
        UpdateTitle();
        RenderPreview();
        ScheduleSettingsSave();
    }

    private void FontFamilyCombo_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => EditorControl_Changed(sender, e);

    private void RenderPreview()
    {
        try
        {
            PreviewCanvas.Width = _project.Canvas.Width;
            PreviewCanvas.Height = _project.Canvas.Height;
            PreviewImage.Width = _project.Canvas.Width;
            PreviewImage.Height = _project.Canvas.Height;
            PreviewImage.Source = _renderer.Render(_project);
            CanvasInfoText.Text = $"{_project.Canvas.Width} × {_project.Canvas.Height} px";
            StatusText.Text = _currentProjectPath is null
                ? "새 작업"
                : Path.GetFileName(_currentProjectPath);
            UpdateSelectionVisual();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"미리보기 오류: {ex.Message}";
        }
    }

    private void NewButton_Click(object sender, RoutedEventArgs e) => NewProject();
    private void OpenButton_Click(object sender, RoutedEventArgs e) => OpenProject();
    private void SaveButton_Click(object sender, RoutedEventArgs e) => SaveProject(false);
    private void SaveAsButton_Click(object sender, RoutedEventArgs e) => SaveProject(true);
    private void ExportButton_Click(object sender, RoutedEventArgs e) => ExportPng();

    private void NewProject()
    {
        if (!ConfirmDiscardIfNeeded())
            return;

        var previous = ReadProjectFromUi();
        previous.Version = LogoProject.CurrentVersion;
        previous.Text.CenterX = previous.Canvas.Width / 2.0;
        previous.Text.CenterY = previous.Canvas.Height / 2.0;
        previous.Image = new ImageSettings
        {
            ScalePercent = previous.Image.ScaleXPercent,
            ScaleXPercent = previous.Image.ScaleXPercent,
            ScaleYPercent = previous.Image.ScaleYPercent,
            PreserveAspectRatio = previous.Image.PreserveAspectRatio,
            CenterX = previous.Canvas.Width / 2.0,
            CenterY = previous.Canvas.Height / 2.0
        };

        _project = previous;
        _currentProjectPath = null;
        _isDirty = false;
        _selectedElement = SelectedElement.None;
        ApplyProjectToUi(_project);
        RenderPreview();
        UpdateTitle();
        ScheduleSettingsSave();
    }

    private void OpenProject()
    {
        if (!ConfirmDiscardIfNeeded())
            return;

        var dialog = new OpenFileDialog
        {
            Title = "DK Ez Logo Maker 프로젝트 열기",
            Filter = "DK Ez Logo Maker 프로젝트 (*.dklm)|*.dklm|모든 파일 (*.*)|*.*",
            InitialDirectory = ExistingDirectoryOrNull(_settings.LastProjectDirectory)
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            _project = _projectService.Load(dialog.FileName);
            _currentProjectPath = dialog.FileName;
            _settings.LastProjectDirectory = Path.GetDirectoryName(dialog.FileName);
            _settings.LastImagePath = null;
            _isDirty = false;
            _selectedElement = SelectedElement.None;
            ApplyProjectToUi(_project);
            RenderPreview();
            UpdateTitle();
            SaveAppSettings();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "프로젝트 열기 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool SaveProject(bool saveAs)
    {
        _project = ReadProjectFromUi();
        var path = _currentProjectPath;

        if (saveAs || string.IsNullOrWhiteSpace(path))
        {
            var dialog = new SaveFileDialog
            {
                Title = "프로젝트 저장",
                Filter = "DK Ez Logo Maker 프로젝트 (*.dklm)|*.dklm",
                DefaultExt = ".dklm",
                AddExtension = true,
                FileName = string.IsNullOrWhiteSpace(path) ? "logo-project.dklm" : Path.GetFileName(path),
                InitialDirectory = ExistingDirectoryOrNull(_settings.LastProjectDirectory)
            };

            if (dialog.ShowDialog(this) != true)
                return false;

            path = dialog.FileName;
        }

        try
        {
            _projectService.Save(path!, _project);
            _currentProjectPath = path;
            _settings.LastProjectDirectory = Path.GetDirectoryName(path);
            _isDirty = false;
            UpdateTitle();
            StatusText.Text = $"저장됨 · {Path.GetFileName(path)}";
            SaveAppSettings();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "프로젝트 저장 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void ExportPng()
    {
        _project = ReadProjectFromUi();

        var options = new ExportOptionsWindow(
            _project.Canvas.Width,
            _project.Canvas.Height,
            _settings.LastExportScalePercent,
            _settings.LastExportScalingMode)
        {
            Owner = this
        };

        if (options.ShowDialog() != true)
            return;

        _settings.LastExportScalePercent = options.ScalePercent;
        _settings.LastExportScalingMode = options.ScalingModeName;

        var suggested = !string.IsNullOrWhiteSpace(_currentProjectPath)
            ? Path.GetFileNameWithoutExtension(_currentProjectPath) + ".png"
            : "logo.png";

        var dialog = new SaveFileDialog
        {
            Title = "PNG로 내보내기",
            Filter = "PNG 이미지 (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = suggested,
            InitialDirectory = ExistingDirectoryOrNull(_settings.LastExportDirectory)
        };

        if (dialog.ShowDialog(this) != true)
        {
            ScheduleSettingsSave();
            return;
        }

        try
        {
            _renderer.SavePng(dialog.FileName, _project, options.ScalePercent, options.ScalingMode);
            _settings.LastExportDirectory = Path.GetDirectoryName(dialog.FileName);
            var outputWidth = Math.Max(1, (int)Math.Round(_project.Canvas.Width * options.ScalePercent / 100.0));
            var outputHeight = Math.Max(1, (int)Math.Round(_project.Canvas.Height * options.ScalePercent / 100.0));
            StatusText.Text = $"PNG 저장됨 · {Path.GetFileName(dialog.FileName)} · {outputWidth}×{outputHeight}";
            SaveAppSettings();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "PNG 저장 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "이미지 불러오기",
            Filter = "이미지 (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|모든 파일 (*.*)|*.*",
            InitialDirectory = ExistingDirectoryOrNull(_settings.LastImageDirectory)
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            _project = ReadProjectFromUi();
            _project.Image.RuntimeBytes = File.ReadAllBytes(dialog.FileName);
            _project.Image.RuntimeSourcePath = dialog.FileName;
            _project.Image.SourceFileName = Path.GetFileName(dialog.FileName);
            _project.Image.EmbeddedAssetName = null;
            _settings.LastImagePath = dialog.FileName;
            _settings.LastImageDirectory = Path.GetDirectoryName(dialog.FileName);
            ConstrainProjectToCanvas(_project);
            _isDirty = true;
            _selectedElement = SelectedElement.Image;
            ImageFileNameText.Text = _project.Image.SourceFileName;
            SyncGeometryControlsToProject();
            RenderPreview();
            UpdateTitle();
            ScheduleSettingsSave();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "이미지 불러오기 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearImageButton_Click(object sender, RoutedEventArgs e)
    {
        _project = ReadProjectFromUi();
        _project.Image.RuntimeBytes = null;
        _project.Image.RuntimeSourcePath = null;
        _project.Image.SourceFileName = null;
        _project.Image.EmbeddedAssetName = null;
        _settings.LastImagePath = null;
        _isDirty = true;
        if (_selectedElement == SelectedElement.Image)
            _selectedElement = SelectedElement.None;
        ImageFileNameText.Text = "이미지 없음";
        RenderPreview();
        UpdateTitle();
        ScheduleSettingsSave();
    }

    private void BackgroundColor1Button_Click(object sender, RoutedEventArgs e)
        => PickColor(BackgroundColor1Box);

    private void BackgroundColor2Button_Click(object sender, RoutedEventArgs e)
        => PickColor(BackgroundColor2Box);

    private void TextColorButton_Click(object sender, RoutedEventArgs e)
        => PickColor(TextColorBox);

    private void PickColor(System.Windows.Controls.TextBox target)
    {
        using var dialog = new WinForms.ColorDialog { FullOpen = true };
        if (TryParseMediaColor(target.Text, out var current))
            dialog.Color = System.Drawing.Color.FromArgb(current.A, current.R, current.G, current.B);

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        target.Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        _fitPreview = false;
        SetPreviewZoom(_previewZoom * 1.25);
    }

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        _fitPreview = false;
        SetPreviewZoom(_previewZoom / 1.25);
    }

    private void FitPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        _fitPreview = true;
        FitPreviewToViewport();
    }

    private void ActualSizeButton_Click(object sender, RoutedEventArgs e)
    {
        _fitPreview = false;
        SetPreviewZoom(1.0);
    }

    private void PreviewViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fitPreview)
            FitPreviewToViewport(false);
    }

    private void FitPreviewToViewport(bool persist = true)
    {
        if (_project.Canvas.Width <= 0 || _project.Canvas.Height <= 0)
            return;

        var availableWidth = Math.Max(1.0, PreviewScrollViewer.ActualWidth - 32.0);
        var availableHeight = Math.Max(1.0, PreviewScrollViewer.ActualHeight - 32.0);
        if (availableWidth <= 1.0 || availableHeight <= 1.0)
            return;

        var zoom = Math.Min(
            availableWidth / _project.Canvas.Width,
            availableHeight / _project.Canvas.Height);
        SetPreviewZoom(zoom, persist);
    }

    private void SetPreviewZoom(double zoom, bool persist = true)
    {
        _previewZoom = Math.Clamp(zoom, MinPreviewZoom, MaxPreviewZoom);
        PreviewScaleTransform.ScaleX = _previewZoom;
        PreviewScaleTransform.ScaleY = _previewZoom;
        ZoomText.Text = $"{_previewZoom * 100:0}%";
        UpdateSelectionVisual();

        if (persist)
        {
            _settings.PreviewZoom = _previewZoom;
            ScheduleSettingsSave();
        }
    }

    private void PreviewCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        _project = ReadProjectFromUi();
        var point = e.GetPosition(PreviewCanvas);
        var selectedBounds = GetSelectedBounds();

        if (_selectedElement != SelectedElement.None && !selectedBounds.IsEmpty)
        {
            var handle = HitTestResizeHandle(selectedBounds, point);
            if (handle is not null)
            {
                BeginInteraction(InteractionMode.Resize, point, selectedBounds, handle.Value);
                e.Handled = true;
                return;
            }
        }

        var textBounds = _renderer.GetTextBounds(_project);
        var imageBounds = _renderer.GetImageBounds(_project);

        if (!textBounds.IsEmpty && textBounds.Contains(point))
            _selectedElement = SelectedElement.Text;
        else if (!imageBounds.IsEmpty && imageBounds.Contains(point))
            _selectedElement = SelectedElement.Image;
        else
            _selectedElement = SelectedElement.None;

        UpdateSelectionVisual();

        if (_selectedElement != SelectedElement.None)
        {
            BeginInteraction(InteractionMode.Move, point, GetSelectedBounds());
            e.Handled = true;
        }
    }

    private void BeginInteraction(InteractionMode mode, Point point, Rect bounds, ResizeHandle handle = ResizeHandle.BottomRight)
    {
        _interactionMode = mode;
        _pointerStart = point;
        _startBounds = bounds;
        _resizeHandle = handle;

        if (_selectedElement == SelectedElement.Text)
        {
            _startCenterX = _project.Text.CenterX;
            _startCenterY = _project.Text.CenterY;
            _startTextSize = _project.Text.Size;
        }
        else if (_selectedElement == SelectedElement.Image)
        {
            _startCenterX = _project.Image.CenterX;
            _startCenterY = _project.Image.CenterY;
            _startImageScaleX = _project.Image.ScaleXPercent;
            _startImageScaleY = _project.Image.ScaleYPercent;
        }

        SetInteractionCursor();
        PreviewCanvas.CaptureMouse();
    }

    private void PreviewCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(PreviewCanvas);

        if (_interactionMode == InteractionMode.None)
        {
            UpdatePointerCursor(point);
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        SetInteractionCursor();

        if (_interactionMode == InteractionMode.Move)
            ApplyMoveInteraction(point);
        else if (_interactionMode == InteractionMode.Resize)
            ApplyResizeInteraction(point);

        ApplyInteractionToUi();
        MarkInteractiveChange();
        e.Handled = true;
    }

    private void PreviewCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_interactionMode == InteractionMode.None)
            PreviewCanvas.Cursor = Cursors.Arrow;
    }

    private void PreviewCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _project = ReadProjectFromUi();
        var point = e.GetPosition(PreviewCanvas);
        var element = HitTestElement(point);
        if (element == SelectedElement.None)
            return;

        _selectedElement = element;
        UpdateSelectionVisual();

        var menu = BuildElementContextMenu(element);
        menu.PlacementTarget = PreviewCanvas;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private ContextMenu BuildElementContextMenu(SelectedElement element)
    {
        var menu = new ContextMenu();

        var title = new MenuItem
        {
            Header = element == SelectedElement.Text ? "글자" : "이미지",
            IsEnabled = false,
            FontWeight = FontWeights.SemiBold
        };
        menu.Items.Add(title);
        menu.Items.Add(new Separator());

        if (element == SelectedElement.Text)
        {
            var weightMenu = new MenuItem { Header = "굵기" };
            foreach (var (label, value) in new[]
            {
                ("Regular", "Regular"),
                ("Medium", "Medium"),
                ("SemiBold", "SemiBold"),
                ("Bold", "Bold")
            })
            {
                var item = new MenuItem
                {
                    Header = label,
                    IsCheckable = true,
                    IsChecked = string.Equals(_project.Text.Weight, value, StringComparison.OrdinalIgnoreCase)
                };
                item.Click += (_, _) => ApplyTextWeight(value);
                weightMenu.Items.Add(item);
            }
            menu.Items.Add(weightMenu);

            var spacingMenu = new MenuItem { Header = "글자 간격" };
            foreach (var (label, value) in new[]
            {
                ("좁게 (-2 px)", -2.0),
                ("기본 (0 px)", 0.0),
                ("넓게 (2 px)", 2.0),
                ("매우 넓게 (4 px)", 4.0)
            })
            {
                var item = new MenuItem
                {
                    Header = label,
                    IsCheckable = true,
                    IsChecked = Math.Abs(_project.Text.Spacing - value) < 0.001
                };
                item.Click += (_, _) => ApplyTextSpacing(value);
                spacingMenu.Items.Add(item);
            }
            menu.Items.Add(spacingMenu);
        }
        else
        {
            var preserveItem = new MenuItem
            {
                Header = "가로·세로 비율 유지",
                IsCheckable = true,
                IsChecked = _project.Image.PreserveAspectRatio
            };
            preserveItem.Click += (_, _) => ApplyImagePreserveAspect(preserveItem.IsChecked);
            menu.Items.Add(preserveItem);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(BuildAlignmentContextMenu(element, true));
        menu.Items.Add(BuildAlignmentContextMenu(element, false));
        return menu;
    }

    private MenuItem BuildAlignmentContextMenu(SelectedElement element, bool horizontal)
    {
        var menu = new MenuItem { Header = horizontal ? "가로 정렬" : "세로 정렬" };
        var options = horizontal
            ? new[] { ("왼쪽", "Start"), ("중앙", "Center"), ("오른쪽", "End") }
            : new[] { ("위", "Start"), ("중앙", "Center"), ("아래", "End") };

        foreach (var (label, mode) in options)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => ApplyAlignment(element, horizontal, mode);
            menu.Items.Add(item);
        }

        return menu;
    }

    private void ApplyTextWeight(string weight)
    {
        _project = ReadProjectFromUi();
        _project.Text.Weight = weight;
        _selectedElement = SelectedElement.Text;
        SelectComboByTag(FontWeightCombo, weight);
        ConstrainProjectToCanvas(_project);
        SyncGeometryControlsToProject();
        MarkInteractiveChange();
    }

    private void ApplyTextSpacing(double spacing)
    {
        _project = ReadProjectFromUi();
        _project.Text.Spacing = spacing;
        _selectedElement = SelectedElement.Text;
        ConstrainProjectToCanvas(_project);
        SyncGeometryControlsToProject();
        MarkInteractiveChange();
    }

    private void ApplyImagePreserveAspect(bool preserve)
    {
        _project = ReadProjectFromUi();
        _project.Image.PreserveAspectRatio = preserve;
        if (preserve)
            _project.Image.ScaleYPercent = _project.Image.ScaleXPercent;
        _project.Image.ScalePercent = _project.Image.ScaleXPercent;
        _selectedElement = SelectedElement.Image;
        ConstrainProjectToCanvas(_project);
        SyncGeometryControlsToProject();
        UpdateImageAspectUi();
        MarkInteractiveChange();
    }

    private SelectedElement HitTestElement(Point point)
    {
        var textBounds = _renderer.GetTextBounds(_project);
        if (!textBounds.IsEmpty && textBounds.Contains(point))
            return SelectedElement.Text;

        var imageBounds = _renderer.GetImageBounds(_project);
        if (!imageBounds.IsEmpty && imageBounds.Contains(point))
            return SelectedElement.Image;

        return SelectedElement.None;
    }

    private void UpdatePointerCursor(Point point)
    {
        if (_selectedElement != SelectedElement.None)
        {
            var selectedBounds = GetSelectedBounds();
            if (!selectedBounds.IsEmpty)
            {
                var handle = HitTestResizeHandle(selectedBounds, point);
                if (handle is not null)
                {
                    PreviewCanvas.Cursor = GetResizeCursor(handle.Value);
                    return;
                }
            }
        }

        PreviewCanvas.Cursor = HitTestElement(point) != SelectedElement.None
            ? Cursors.SizeAll
            : Cursors.Arrow;
    }

    private void SetInteractionCursor()
    {
        PreviewCanvas.Cursor = _interactionMode switch
        {
            InteractionMode.Move => Cursors.SizeAll,
            InteractionMode.Resize => GetResizeCursor(_resizeHandle),
            _ => Cursors.Arrow
        };
    }

    private static Cursor GetResizeCursor(ResizeHandle handle)
        => handle switch
        {
            ResizeHandle.Top or ResizeHandle.Bottom => Cursors.SizeNS,
            ResizeHandle.Left or ResizeHandle.Right => Cursors.SizeWE,
            ResizeHandle.TopLeft or ResizeHandle.BottomRight => Cursors.SizeNWSE,
            ResizeHandle.TopRight or ResizeHandle.BottomLeft => Cursors.SizeNESW,
            _ => Cursors.Arrow
        };

    private void ApplyMoveInteraction(Point point)
    {
        var dx = point.X - _pointerStart.X;
        var dy = point.Y - _pointerStart.Y;

        if (_selectedElement == SelectedElement.Text)
        {
            _project.Text.CenterX = _startCenterX + dx;
            _project.Text.CenterY = _startCenterY + dy;
        }
        else if (_selectedElement == SelectedElement.Image)
        {
            _project.Image.CenterX = _startCenterX + dx;
            _project.Image.CenterY = _startCenterY + dy;
        }

        ClampSelectedElementPosition();
    }

    private void ApplyResizeInteraction(Point point)
    {
        if (_startBounds.IsEmpty || _startBounds.Width <= 0 || _startBounds.Height <= 0)
            return;

        var movesLeft = _resizeHandle is ResizeHandle.TopLeft or ResizeHandle.Left or ResizeHandle.BottomLeft;
        var movesRight = _resizeHandle is ResizeHandle.TopRight or ResizeHandle.Right or ResizeHandle.BottomRight;
        var movesTop = _resizeHandle is ResizeHandle.TopLeft or ResizeHandle.Top or ResizeHandle.TopRight;
        var movesBottom = _resizeHandle is ResizeHandle.BottomLeft or ResizeHandle.Bottom or ResizeHandle.BottomRight;
        var resizeHorizontal = movesLeft || movesRight;
        var resizeVertical = movesTop || movesBottom;

        var desiredWidth = _startBounds.Width;
        var desiredHeight = _startBounds.Height;

        if (movesLeft)
            desiredWidth = Math.Max(0.001, _startBounds.Right - point.X);
        else if (movesRight)
            desiredWidth = Math.Max(0.001, point.X - _startBounds.Left);

        if (movesTop)
            desiredHeight = Math.Max(0.001, _startBounds.Bottom - point.Y);
        else if (movesBottom)
            desiredHeight = Math.Max(0.001, point.Y - _startBounds.Top);

        var maxWidth = movesLeft
            ? _startBounds.Right
            : movesRight
                ? _project.Canvas.Width - _startBounds.Left
                : 2.0 * Math.Min((_startBounds.Left + _startBounds.Width / 2.0), _project.Canvas.Width - (_startBounds.Left + _startBounds.Width / 2.0));
        var maxHeight = movesTop
            ? _startBounds.Bottom
            : movesBottom
                ? _project.Canvas.Height - _startBounds.Top
                : 2.0 * Math.Min((_startBounds.Top + _startBounds.Height / 2.0), _project.Canvas.Height - (_startBounds.Top + _startBounds.Height / 2.0));

        maxWidth = Math.Max(0.001, maxWidth);
        maxHeight = Math.Max(0.001, maxHeight);
        desiredWidth = Math.Min(desiredWidth, maxWidth);
        desiredHeight = Math.Min(desiredHeight, maxHeight);

        if (_selectedElement == SelectedElement.Text)
        {
            var ratio = resizeHorizontal && resizeVertical
                ? SelectDominantResizeRatio(desiredWidth / _startBounds.Width, desiredHeight / _startBounds.Height)
                : resizeHorizontal
                    ? desiredWidth / _startBounds.Width
                    : desiredHeight / _startBounds.Height;

            var maxRatio = Math.Min(maxWidth / _startBounds.Width, maxHeight / _startBounds.Height);
            ratio = Math.Clamp(ratio, 0.001, Math.Max(0.001, maxRatio));

            _project.Text.Size = Math.Clamp(_startTextSize * ratio, 0.1, 2048.0);
            var newWidth = _startBounds.Width * ratio;
            var newHeight = _startBounds.Height * ratio;
            var (centerX, centerY) = GetResizedCenter(newWidth, newHeight, movesLeft, movesRight, movesTop, movesBottom);
            _project.Text.CenterX = centerX;
            _project.Text.CenterY = centerY;
        }
        else if (_selectedElement == SelectedElement.Image)
        {
            if (_project.Image.PreserveAspectRatio)
            {
                var ratio = resizeHorizontal && resizeVertical
                    ? SelectDominantResizeRatio(desiredWidth / _startBounds.Width, desiredHeight / _startBounds.Height)
                    : resizeHorizontal
                        ? desiredWidth / _startBounds.Width
                        : desiredHeight / _startBounds.Height;

                var maxRatio = Math.Min(maxWidth / _startBounds.Width, maxHeight / _startBounds.Height);
                ratio = Math.Clamp(ratio, 0.001, Math.Max(0.001, maxRatio));

                _project.Image.ScaleXPercent = Math.Clamp(_startImageScaleX * ratio, 0.001, 5000.0);
                _project.Image.ScaleYPercent = Math.Clamp(_startImageScaleY * ratio, 0.001, 5000.0);
                var newWidth = _startBounds.Width * ratio;
                var newHeight = _startBounds.Height * ratio;
                var (centerX, centerY) = GetResizedCenter(newWidth, newHeight, movesLeft, movesRight, movesTop, movesBottom);
                _project.Image.CenterX = centerX;
                _project.Image.CenterY = centerY;
            }
            else
            {
                var ratioX = resizeHorizontal ? desiredWidth / _startBounds.Width : 1.0;
                var ratioY = resizeVertical ? desiredHeight / _startBounds.Height : 1.0;
                ratioX = Math.Clamp(ratioX, 0.001, Math.Max(0.001, maxWidth / _startBounds.Width));
                ratioY = Math.Clamp(ratioY, 0.001, Math.Max(0.001, maxHeight / _startBounds.Height));

                _project.Image.ScaleXPercent = Math.Clamp(_startImageScaleX * ratioX, 0.001, 5000.0);
                _project.Image.ScaleYPercent = Math.Clamp(_startImageScaleY * ratioY, 0.001, 5000.0);
                var newWidth = _startBounds.Width * ratioX;
                var newHeight = _startBounds.Height * ratioY;
                var (centerX, centerY) = GetResizedCenter(newWidth, newHeight, movesLeft, movesRight, movesTop, movesBottom);
                _project.Image.CenterX = centerX;
                _project.Image.CenterY = centerY;
            }

            _project.Image.ScalePercent = _project.Image.ScaleXPercent;
        }

        ConstrainProjectToCanvas(_project);
    }

    private static double SelectDominantResizeRatio(double ratioX, double ratioY)
        => Math.Abs(ratioX - 1.0) >= Math.Abs(ratioY - 1.0) ? ratioX : ratioY;

    private (double X, double Y) GetResizedCenter(
        double width,
        double height,
        bool movesLeft,
        bool movesRight,
        bool movesTop,
        bool movesBottom)
    {
        var centerX = movesLeft
            ? _startBounds.Right - width / 2.0
            : movesRight
                ? _startBounds.Left + width / 2.0
                : _startBounds.Left + _startBounds.Width / 2.0;

        var centerY = movesTop
            ? _startBounds.Bottom - height / 2.0
            : movesBottom
                ? _startBounds.Top + height / 2.0
                : _startBounds.Top + _startBounds.Height / 2.0;

        return (centerX, centerY);
    }

    private void ApplyInteractionToUi()
    {
        _suppressChanges = true;
        try
        {
            if (_selectedElement == SelectedElement.Text)
            {
                TextSizeBox.Text = FormatNumber(_project.Text.Size);
                TextCenterXBox.Text = FormatNumber(_project.Text.CenterX);
                TextCenterYBox.Text = FormatNumber(_project.Text.CenterY);
            }
            else if (_selectedElement == SelectedElement.Image)
            {
                ImageScaleXBox.Text = FormatNumber(_project.Image.ScaleXPercent);
                ImageScaleYBox.Text = FormatNumber(_project.Image.ScaleYPercent);
                ImagePreserveAspectCheckBox.IsChecked = _project.Image.PreserveAspectRatio;
                ImageCenterXBox.Text = FormatNumber(_project.Image.CenterX);
                ImageCenterYBox.Text = FormatNumber(_project.Image.CenterY);
            }
        }
        finally
        {
            _suppressChanges = false;
        }
    }

    private void MarkInteractiveChange()
    {
        _isDirty = true;
        UpdateTitle();
        RenderPreview();
        ScheduleSettingsSave();
    }

    private void PreviewCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_interactionMode == InteractionMode.None)
            return;

        _interactionMode = InteractionMode.None;
        PreviewCanvas.ReleaseMouseCapture();
        UpdatePointerCursor(e.GetPosition(PreviewCanvas));
        e.Handled = true;
    }

    private Rect GetSelectedBounds() => _selectedElement switch
    {
        SelectedElement.Text => _renderer.GetTextBounds(_project),
        SelectedElement.Image => _renderer.GetImageBounds(_project),
        _ => Rect.Empty
    };

    private ResizeHandle? HitTestResizeHandle(Rect bounds, Point point)
    {
        foreach (var corner in new[]
        {
            ResizeHandle.TopLeft,
            ResizeHandle.TopRight,
            ResizeHandle.BottomLeft,
            ResizeHandle.BottomRight
        })
        {
            if (GetResizeHandleBounds(bounds, corner).Contains(point))
                return corner;
        }

        var edgeHitSize = 10.0 / Math.Max(_previewZoom, MinPreviewZoom);
        var half = edgeHitSize / 2.0;

        if (new Rect(bounds.Left - half, bounds.Top + half, edgeHitSize, Math.Max(0, bounds.Height - edgeHitSize)).Contains(point))
            return ResizeHandle.Left;
        if (new Rect(bounds.Right - half, bounds.Top + half, edgeHitSize, Math.Max(0, bounds.Height - edgeHitSize)).Contains(point))
            return ResizeHandle.Right;
        if (new Rect(bounds.Left + half, bounds.Top - half, Math.Max(0, bounds.Width - edgeHitSize), edgeHitSize).Contains(point))
            return ResizeHandle.Top;
        if (new Rect(bounds.Left + half, bounds.Bottom - half, Math.Max(0, bounds.Width - edgeHitSize), edgeHitSize).Contains(point))
            return ResizeHandle.Bottom;

        return null;
    }

    private Rect GetResizeHandleBounds(Rect bounds, ResizeHandle handle)
    {
        var handleSize = 14.0 / Math.Max(_previewZoom, MinPreviewZoom);
        var center = handle switch
        {
            ResizeHandle.TopLeft => bounds.TopLeft,
            ResizeHandle.TopRight => bounds.TopRight,
            ResizeHandle.BottomLeft => bounds.BottomLeft,
            ResizeHandle.BottomRight => bounds.BottomRight,
            ResizeHandle.Top => new Point(bounds.Left + bounds.Width / 2.0, bounds.Top),
            ResizeHandle.Right => new Point(bounds.Right, bounds.Top + bounds.Height / 2.0),
            ResizeHandle.Bottom => new Point(bounds.Left + bounds.Width / 2.0, bounds.Bottom),
            ResizeHandle.Left => new Point(bounds.Left, bounds.Top + bounds.Height / 2.0),
            _ => bounds.BottomRight
        };

        return new Rect(center.X - handleSize / 2.0, center.Y - handleSize / 2.0, handleSize, handleSize);
    }

    private void UpdateSelectionVisual()
    {
        var bounds = GetSelectedBounds();
        if (_selectedElement == SelectedElement.None || bounds.IsEmpty)
        {
            SelectionBorder.Visibility = Visibility.Collapsed;
            SetResizeHandlesVisibility(Visibility.Collapsed);
            PreviewCanvas.Cursor = Cursors.Arrow;
            return;
        }

        SelectionBorder.Visibility = Visibility.Visible;
        SetResizeHandlesVisibility(Visibility.Visible);

        SelectionBorder.Width = Math.Max(0, bounds.Width);
        SelectionBorder.Height = Math.Max(0, bounds.Height);
        SelectionBorder.StrokeThickness = 1.5 / Math.Max(_previewZoom, MinPreviewZoom);
        Canvas.SetLeft(SelectionBorder, bounds.Left);
        Canvas.SetTop(SelectionBorder, bounds.Top);

        var handleSize = 10.0 / Math.Max(_previewZoom, MinPreviewZoom);
        SetResizeHandleVisual(ResizeHandleTopLeft, bounds.TopLeft, handleSize);
        SetResizeHandleVisual(ResizeHandleTopRight, bounds.TopRight, handleSize);
        SetResizeHandleVisual(ResizeHandleBottomLeft, bounds.BottomLeft, handleSize);
        SetResizeHandleVisual(ResizeHandleBottomRight, bounds.BottomRight, handleSize);
    }

    private void SetResizeHandlesVisibility(Visibility visibility)
    {
        ResizeHandleTopLeft.Visibility = visibility;
        ResizeHandleTopRight.Visibility = visibility;
        ResizeHandleBottomLeft.Visibility = visibility;
        ResizeHandleBottomRight.Visibility = visibility;
    }

    private void SetResizeHandleVisual(System.Windows.Shapes.Rectangle handle, Point center, double size)
    {
        handle.Width = size;
        handle.Height = size;
        handle.StrokeThickness = 1.0 / Math.Max(_previewZoom, MinPreviewZoom);
        Canvas.SetLeft(handle, center.X - size / 2.0);
        Canvas.SetTop(handle, center.Y - size / 2.0);
    }

    private void AlignmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not string tag)
            return;

        var parts = tag.Split(':');
        if (parts.Length != 3)
            return;

        var element = string.Equals(parts[0], "Text", StringComparison.OrdinalIgnoreCase)
            ? SelectedElement.Text
            : SelectedElement.Image;
        var horizontal = string.Equals(parts[1], "H", StringComparison.OrdinalIgnoreCase);
        ApplyAlignment(element, horizontal, parts[2]);
    }

    private void ApplyAlignment(SelectedElement element, bool horizontal, string mode)
    {
        _project = ReadProjectFromUi();
        _selectedElement = element;

        var bounds = GetSelectedBounds();
        if (bounds.IsEmpty)
            return;

        if (element == SelectedElement.Text)
        {
            if (horizontal)
                _project.Text.CenterX = GetAlignedCenter(mode, bounds.Width, _project.Canvas.Width);
            else
                _project.Text.CenterY = GetAlignedCenter(mode, bounds.Height, _project.Canvas.Height);
        }
        else
        {
            if (horizontal)
                _project.Image.CenterX = GetAlignedCenter(mode, bounds.Width, _project.Canvas.Width);
            else
                _project.Image.CenterY = GetAlignedCenter(mode, bounds.Height, _project.Canvas.Height);
        }

        ConstrainProjectToCanvas(_project);
        SyncGeometryControlsToProject();
        MarkInteractiveChange();
    }

    private static double GetAlignedCenter(string mode, double elementSize, double canvasSize)
        => mode switch
        {
            "Start" => elementSize / 2.0,
            "End" => canvasSize - elementSize / 2.0,
            _ => canvasSize / 2.0
        };

    private void UpdateImageAspectUi()
    {
        var preserve = ImagePreserveAspectCheckBox.IsChecked == true;
        ImageScaleYBox.IsEnabled = !preserve;
        if (preserve && ImageScaleYBox.Text != ImageScaleXBox.Text)
        {
            var previousSuppress = _suppressChanges;
            _suppressChanges = true;
            try { ImageScaleYBox.Text = ImageScaleXBox.Text; }
            finally { _suppressChanges = previousSuppress; }
        }
    }

    private void SyncGeometryControlsToProject()
    {
        _suppressChanges = true;
        try
        {
            CanvasWidthBox.Text = _project.Canvas.Width.ToString(CultureInfo.InvariantCulture);
            CanvasHeightBox.Text = _project.Canvas.Height.ToString(CultureInfo.InvariantCulture);
            TextSizeBox.Text = FormatNumber(_project.Text.Size);
            TextSpacingBox.Text = FormatNumber(_project.Text.Spacing);
            TextCenterXBox.Text = FormatNumber(_project.Text.CenterX);
            TextCenterYBox.Text = FormatNumber(_project.Text.CenterY);
            ImageScaleXBox.Text = FormatNumber(_project.Image.ScaleXPercent);
            ImageScaleYBox.Text = FormatNumber(_project.Image.ScaleYPercent);
            ImagePreserveAspectCheckBox.IsChecked = _project.Image.PreserveAspectRatio;
            ImageCenterXBox.Text = FormatNumber(_project.Image.CenterX);
            ImageCenterYBox.Text = FormatNumber(_project.Image.CenterY);
            ImageScaleYBox.IsEnabled = !_project.Image.PreserveAspectRatio;
        }
        finally
        {
            _suppressChanges = false;
        }
    }

    private void ConstrainProjectToCanvas(LogoProject project)
    {
        project.Canvas.Width = Math.Clamp(project.Canvas.Width, 1, 8192);
        project.Canvas.Height = Math.Clamp(project.Canvas.Height, 1, 8192);

        ConstrainTextToCanvas(project);
        ConstrainImageToCanvas(project);
    }

    private void ConstrainTextToCanvas(LogoProject project)
    {
        if (string.IsNullOrEmpty(project.Text.Value))
        {
            project.Text.CenterX = Math.Clamp(project.Text.CenterX, 0, project.Canvas.Width);
            project.Text.CenterY = Math.Clamp(project.Text.CenterY, 0, project.Canvas.Height);
            return;
        }

        project.Text.Size = Math.Clamp(project.Text.Size, 0.1, 2048.0);
        var bounds = _renderer.GetTextBounds(project);

        if (!bounds.IsEmpty && (bounds.Width > project.Canvas.Width || bounds.Height > project.Canvas.Height))
        {
            var ratio = Math.Min(project.Canvas.Width / Math.Max(bounds.Width, 0.001), project.Canvas.Height / Math.Max(bounds.Height, 0.001));
            project.Text.Size = Math.Max(0.1, project.Text.Size * Math.Min(1.0, ratio) * 0.999);
            bounds = _renderer.GetTextBounds(project);
        }

        if (!bounds.IsEmpty && bounds.Width > project.Canvas.Width && project.Text.Value.Length > 1)
        {
            var excess = bounds.Width - project.Canvas.Width;
            project.Text.Spacing = Math.Max(-200.0, project.Text.Spacing - excess / (project.Text.Value.Length - 1));
            bounds = _renderer.GetTextBounds(project);
        }

        if (bounds.IsEmpty)
            return;

        project.Text.CenterX = ClampCenter(project.Text.CenterX, bounds.Width, project.Canvas.Width);
        project.Text.CenterY = ClampCenter(project.Text.CenterY, bounds.Height, project.Canvas.Height);
    }

    private void ConstrainImageToCanvas(LogoProject project)
    {
        project.Image.ScaleXPercent = Math.Clamp(project.Image.ScaleXPercent, 0.001, 5000.0);
        project.Image.ScaleYPercent = Math.Clamp(project.Image.ScaleYPercent, 0.001, 5000.0);

        if (project.Image.PreserveAspectRatio)
            project.Image.ScaleYPercent = project.Image.ScaleXPercent;

        var bounds = _renderer.GetImageBounds(project);
        if (!bounds.IsEmpty)
        {
            if (project.Image.PreserveAspectRatio && (bounds.Width > project.Canvas.Width || bounds.Height > project.Canvas.Height))
            {
                var ratio = Math.Min(project.Canvas.Width / Math.Max(bounds.Width, 0.001), project.Canvas.Height / Math.Max(bounds.Height, 0.001));
                project.Image.ScaleXPercent = Math.Max(0.001, project.Image.ScaleXPercent * Math.Min(1.0, ratio));
                project.Image.ScaleYPercent = Math.Max(0.001, project.Image.ScaleYPercent * Math.Min(1.0, ratio));
                bounds = _renderer.GetImageBounds(project);
            }
            else if (!project.Image.PreserveAspectRatio)
            {
                if (bounds.Width > project.Canvas.Width)
                    project.Image.ScaleXPercent = Math.Max(0.001, project.Image.ScaleXPercent * project.Canvas.Width / Math.Max(bounds.Width, 0.001));
                if (bounds.Height > project.Canvas.Height)
                    project.Image.ScaleYPercent = Math.Max(0.001, project.Image.ScaleYPercent * project.Canvas.Height / Math.Max(bounds.Height, 0.001));
                bounds = _renderer.GetImageBounds(project);
            }

            project.Image.CenterX = ClampCenter(project.Image.CenterX, bounds.Width, project.Canvas.Width);
            project.Image.CenterY = ClampCenter(project.Image.CenterY, bounds.Height, project.Canvas.Height);
        }
        else
        {
            project.Image.CenterX = Math.Clamp(project.Image.CenterX, 0, project.Canvas.Width);
            project.Image.CenterY = Math.Clamp(project.Image.CenterY, 0, project.Canvas.Height);
        }

        project.Image.ScalePercent = project.Image.ScaleXPercent;
    }

    private void ClampSelectedElementPosition()
    {
        var bounds = GetSelectedBounds();
        if (bounds.IsEmpty)
            return;

        if (_selectedElement == SelectedElement.Text)
        {
            _project.Text.CenterX = ClampCenter(_project.Text.CenterX, bounds.Width, _project.Canvas.Width);
            _project.Text.CenterY = ClampCenter(_project.Text.CenterY, bounds.Height, _project.Canvas.Height);
        }
        else if (_selectedElement == SelectedElement.Image)
        {
            _project.Image.CenterX = ClampCenter(_project.Image.CenterX, bounds.Width, _project.Canvas.Width);
            _project.Image.CenterY = ClampCenter(_project.Image.CenterY, bounds.Height, _project.Canvas.Height);
        }
    }

    private static double ClampCenter(double center, double elementSize, double canvasSize)
    {
        if (elementSize >= canvasSize)
            return canvasSize / 2.0;

        var half = elementSize / 2.0;
        return Math.Clamp(center, half, canvasSize - half);
    }

    private bool ConfirmDiscardIfNeeded()
    {
        if (!_isDirty)
            return true;

        var result = MessageBox.Show(this,
            "현재 작업에 저장되지 않은 변경 내용이 있습니다. 저장하시겠습니까?",
            "DK Ez Logo Maker",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        return result switch
        {
            MessageBoxResult.Yes => SaveProject(false),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private void UpdateBackgroundPanels()
    {
        var mode = ParseEnumTag(BackgroundModeCombo, BackgroundMode.Transparent);
        var isSolidOrGradient = mode != BackgroundMode.Transparent;
        BackgroundColor1Box.IsEnabled = isSolidOrGradient;
        BackgroundColor1Button.IsEnabled = isSolidOrGradient;
        GradientColor2Panel.Visibility = mode == BackgroundMode.Gradient ? Visibility.Visible : Visibility.Collapsed;
        GradientDirectionPanel.Visibility = mode == BackgroundMode.Gradient ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScheduleSettingsSave()
    {
        if (!IsLoaded)
            return;

        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void SaveAppSettings()
    {
        if (!IsLoaded)
            return;

        _project = ReadProjectFromUi();
        _settings.EditorState = _project;
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.WindowWidth = ActualWidth;
        _settings.WindowHeight = ActualHeight;
        _settings.PreviewZoom = _previewZoom;
        _settings.LastImagePath = _project.Image.RuntimeSourcePath;
        _settingsService.Save(_settings);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmDiscardIfNeeded())
        {
            e.Cancel = true;
            return;
        }

        _settingsSaveTimer.Stop();
        SaveAppSettings();
    }

    private void UpdateTitle()
    {
        var name = string.IsNullOrWhiteSpace(_currentProjectPath)
            ? "새 작업"
            : Path.GetFileName(_currentProjectPath);
        Title = $"DK Ez Logo Maker - {name}{(_isDirty ? " *" : string.Empty)}";
    }

    private static void SelectComboByTag(System.Windows.Controls.ComboBox combo, string value)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private static string? GetSelectedTag(System.Windows.Controls.ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private static T ParseEnumTag<T>(System.Windows.Controls.ComboBox combo, T fallback) where T : struct, Enum
    {
        var tag = GetSelectedTag(combo);
        return Enum.TryParse<T>(tag, true, out var value) ? value : fallback;
    }

    private static int ParseInt(string? text, int fallback, int min, int max)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;

    private static double ParseDouble(string? text, double fallback, double min, double max)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;

    private static string FormatNumber(double value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string NormalizeColorText(string? value, string fallback)
        => TryParseMediaColor(value, out _) ? value!.Trim().ToUpperInvariant() : fallback;

    private static bool TryParseMediaColor(string? value, out System.Windows.Media.Color color)
    {
        try
        {
            if (ColorConverter.ConvertFromString(value) is System.Windows.Media.Color parsed)
            {
                color = parsed;
                return true;
            }
        }
        catch
        {
        }

        color = Colors.Transparent;
        return false;
    }

    private static void UpdateColorButton(System.Windows.Controls.Button button, string colorText)
    {
        button.Background = TryParseMediaColor(colorText, out var color)
            ? new SolidColorBrush(color)
            : Brushes.Transparent;
    }

    private static string? ExistingDirectoryOrNull(string? path)
        => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path : null;

    private enum SelectedElement
    {
        None,
        Text,
        Image
    }

    private enum ResizeHandle
    {
        TopLeft,
        Top,
        TopRight,
        Right,
        BottomRight,
        Bottom,
        BottomLeft,
        Left
    }

    private enum InteractionMode
    {
        None,
        Move,
        Resize
    }

    private sealed class RelayCommand(Action<object?> execute) : ICommand
    {
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute(parameter);
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }
    }
}
