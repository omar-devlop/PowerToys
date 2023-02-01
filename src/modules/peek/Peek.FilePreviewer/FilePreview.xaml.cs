// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Peek.Common.Helpers;
using Peek.Common.Models;
using Peek.FilePreviewer.Models;
using Peek.FilePreviewer.Previewers;
using Windows.ApplicationModel.Resources;
using Windows.Foundation;

namespace Peek.FilePreviewer
{
    [INotifyPropertyChanged]
    public sealed partial class FilePreview : UserControl
    {
        private readonly PreviewerFactory previewerFactory = new();

        public event EventHandler<PreviewSizeChangedArgs>? PreviewSizeChanged;

        public static readonly DependencyProperty FilesProperty =
        DependencyProperty.Register(
            nameof(File),
            typeof(File),
            typeof(FilePreview),
            new PropertyMetadata(false, async (d, e) => await ((FilePreview)d).OnFilePropertyChanged()));

        public static readonly DependencyProperty ScalingFactorProperty =
            DependencyProperty.Register(
                nameof(ScalingFactor),
                typeof(double),
                typeof(FilePreview),
                new PropertyMetadata(false, async (d, e) => await ((FilePreview)d).OnScalingFactorPropertyChanged()));

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(BitmapPreviewer))]
        [NotifyPropertyChangedFor(nameof(BrowserPreviewer))]
        [NotifyPropertyChangedFor(nameof(UnsupportedFilePreviewer))]

        private IPreviewer? previewer;

        [ObservableProperty]
        private string imageInfoTooltip = ResourceLoader.GetForViewIndependentUse().GetString("PreviewTooltip_Blank");

        private CancellationTokenSource _cancellationTokenSource = new();

        public FilePreview()
        {
            InitializeComponent();
        }

        private async void Previewer_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // Fallback on DefaultPreviewer if we fail to load the correct Preview
            if (e.PropertyName == nameof(IPreviewer.State))
            {
                if (Previewer?.State == PreviewState.Error)
                {
                    // Cancel previous loading task
                    _cancellationTokenSource.Cancel();
                    _cancellationTokenSource = new();

                    Previewer = previewerFactory.CreateDefaultPreviewer(File);
                    await UpdatePreviewAsync(_cancellationTokenSource.Token);
                }
            }
        }

        public IBitmapPreviewer? BitmapPreviewer => Previewer as IBitmapPreviewer;

        public IBrowserPreviewer? BrowserPreviewer => Previewer as IBrowserPreviewer;

        public bool IsImageVisible => BitmapPreviewer != null;

        public IUnsupportedFilePreviewer? UnsupportedFilePreviewer => Previewer as IUnsupportedFilePreviewer;

        public bool IsUnsupportedPreviewVisible => UnsupportedFilePreviewer != null;

        public File File
        {
            get => (File)GetValue(FilesProperty);
            set => SetValue(FilesProperty, value);
        }

        public double ScalingFactor
        {
            get => (double)GetValue(ScalingFactorProperty);
            set => SetValue(ScalingFactorProperty, value);
        }

        public bool MatchPreviewState(PreviewState? value, PreviewState stateToMatch)
        {
            return value == stateToMatch;
        }

        public Visibility IsPreviewVisible(IPreviewer? previewer, PreviewState? state)
        {
            var isValidPreview = previewer != null && MatchPreviewState(state, PreviewState.Loaded);
            return isValidPreview ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task OnFilePropertyChanged()
        {
            // Cancel previous loading task
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource = new();

            // TODO: track and cancel existing async preview tasks
            // https://github.com/microsoft/PowerToys/issues/22480
            if (File == null)
            {
                Previewer = null;
                ImagePreview.Visibility = Visibility.Collapsed;
                BrowserPreview.Visibility = Visibility.Collapsed;
                UnsupportedFilePreview.Visibility = Visibility.Collapsed;
                return;
            }

            Previewer = previewerFactory.Create(File);
            if (Previewer is IBitmapPreviewer bitmapPreviewer)
            {
                bitmapPreviewer.ScalingFactor = ScalingFactor;
            }

            await UpdatePreviewAsync(_cancellationTokenSource.Token);
        }

        private async Task OnScalingFactorPropertyChanged()
        {
            // Cancel previous loading task
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource = new();

            await UpdatePreviewAsync(_cancellationTokenSource.Token);
        }

        private async Task UpdatePreviewAsync(CancellationToken cancellationToken)
        {
            if (Previewer != null)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var size = await Previewer.GetPreviewSizeAsync(cancellationToken);
                    SizeFormat windowSizeFormat = UnsupportedFilePreviewer != null ? SizeFormat.Percentage : SizeFormat.Pixels;
                    PreviewSizeChanged?.Invoke(this, new PreviewSizeChangedArgs(size, windowSizeFormat));
                    cancellationToken.ThrowIfCancellationRequested();
                    await Previewer.LoadPreviewAsync(cancellationToken);

                    cancellationToken.ThrowIfCancellationRequested();
                    await UpdateImageTooltipAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // TODO: Log task cancelled exception?
                }
            }
        }

        partial void OnPreviewerChanging(IPreviewer? value)
        {
            if (Previewer != null)
            {
                Previewer.PropertyChanged -= Previewer_PropertyChanged;
            }

            if (value != null)
            {
                value.PropertyChanged += Previewer_PropertyChanged;
            }
        }

        private void BrowserPreview_DOMContentLoaded(Microsoft.Web.WebView2.Core.CoreWebView2 sender, Microsoft.Web.WebView2.Core.CoreWebView2DOMContentLoadedEventArgs args)
        {
            /*
             * There is an odd behavior where the WebView2 would not raise the NavigationCompleted event
             * for certain HTML files, even though it has already been loaded. Probably related to certain
             * extra module that require more time to load. One example is saving and opening google.com locally.
             *
             * So to address this, we will make the Browser visible and display it as "Loaded" as soon the HTML document
             * has been parsed and loaded with the DOMContentLoaded event.
             *
             * Similar issue: https://github.com/MicrosoftEdge/WebView2Feedback/issues/998
             */
            if (BrowserPreviewer != null)
            {
                BrowserPreviewer.State = PreviewState.Loaded;
            }
        }

        private void PreviewBrowser_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            /*
             * In theory most of navigation should work after DOM is loaded.
             * But in case something fails we check NavigationCompleted event
             * for failure and switch visibility accordingly.
             *
             * As an alternative, in the future, the preview Browser control
             * could also display error content.
             */
            if (!args.IsSuccess)
            {
                if (BrowserPreviewer != null)
                {
                    BrowserPreviewer.State = PreviewState.Error;
                }
            }
        }

        private async void KeyboardAccelerator_CtrlC_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            if (Previewer != null)
            {
                await Previewer.CopyAsync();
            }
        }

        private async Task UpdateImageTooltipAsync(CancellationToken cancellationToken)
        {
            if (File == null)
            {
                return;
            }

            // Fetch and format available file properties
            var sb = new StringBuilder();

            string fileNameFormatted = ReadableStringHelper.FormatResourceString("PreviewTooltip_FileName", File.FileName);
            sb.Append(fileNameFormatted);

            cancellationToken.ThrowIfCancellationRequested();
            string fileType = await PropertyHelper.GetFileType(File.Path);
            string fileTypeFormatted = string.IsNullOrEmpty(fileType) ? string.Empty : "\n" + ReadableStringHelper.FormatResourceString("PreviewTooltip_FileType", fileType);
            sb.Append(fileTypeFormatted);

            string dateModified = File.DateModified.ToString();
            string dateModifiedFormatted = string.IsNullOrEmpty(dateModified) ? string.Empty : "\n" + ReadableStringHelper.FormatResourceString("PreviewTooltip_DateModified", dateModified);
            sb.Append(dateModifiedFormatted);

            cancellationToken.ThrowIfCancellationRequested();
            Size dimensions = await PropertyHelper.GetImageSize(File.Path);
            string dimensionsFormatted = dimensions.IsEmpty ? string.Empty : "\n" + ReadableStringHelper.FormatResourceString("PreviewTooltip_Dimensions", dimensions.Width, dimensions.Height);
            sb.Append(dimensionsFormatted);

            cancellationToken.ThrowIfCancellationRequested();
            ulong bytes = await PropertyHelper.GetFileSizeInBytes(File.Path);
            string fileSize = ReadableStringHelper.BytesToReadableString(bytes);
            string fileSizeFormatted = string.IsNullOrEmpty(fileSize) ? string.Empty : "\n" + ReadableStringHelper.FormatResourceString("PreviewTooltip_FileSize", fileSize);
            sb.Append(fileSizeFormatted);

            ImageInfoTooltip = sb.ToString();
        }
    }
}