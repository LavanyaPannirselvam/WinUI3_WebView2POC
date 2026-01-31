using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WInUI3_POC
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private readonly string _actionLogFile = @"D:\source\WInUI3_POC\WebView2UserAction.log";
        private DispatcherQueueTimer _uiFreezeTimer;
        private DateTime _lastUiHeartbeat;
        private Timer _backgroundWatchdog;
        private const int FreezeThresholdMs = 2000;
        public MainWindow()
        {
            InitializeComponent();
            SetupUiFreezeDetection();
            InitializeWebView();
        }

        private void SetupUiFreezeDetection()
        {
            _lastUiHeartbeat = DateTime.UtcNow;

            // UI thread heartbeat - updates timestamp every 500ms
            _uiFreezeTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _uiFreezeTimer.Interval = TimeSpan.FromMilliseconds(500);
            _uiFreezeTimer.Tick += (s, e) =>
            {
                _lastUiHeartbeat = DateTime.UtcNow;
            };
            _uiFreezeTimer.Start();

            // Background watchdog - checks if UI thread is responsive
            _backgroundWatchdog = new Timer(_ =>
            {
                var elapsed = (DateTime.UtcNow - _lastUiHeartbeat).TotalMilliseconds;
                if (elapsed > FreezeThresholdMs)
                {
                    LogAction($"[UI FREEZE DETECTED] UI thread is unresponsive");
                }
            }, null, 1000, 500);

            Debug.WriteLine("[SETUP] UI freeze detection initialized");
        }

        private void LogAction(string message)
        {
            string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";

            try
            {
                File.AppendAllText(_actionLogFile, logEntry + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to write to log file: {ex.Message}");
            }
        }

        private async void InitializeWebView()
        {
            try
            {
                LogAction("WebView2 initialization started");
                MyWebView.CoreWebView2Initialized += MyWebView_CoreWebView2Initialized;

                var options = new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments = "--enable-logging --v=1 --log-file=D:\\source\\WInUI3_POC\\WebView2.log "
                };
                var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, null, options);
                await MyWebView.EnsureCoreWebView2Async(env);

                LogAction("WebView2 EnsureCoreWebView2Async completed");

                WebView2DiagnosticEvents();
                SetupActionLogging();
            }
            catch (Exception ex)
            {
                LogAction($"ERROR: Initialization failed - {ex.Message}");
                LogAction($"ERROR: Stack trace - {ex.StackTrace}");
                return;
            }

            string html = @"<!DOCTYPE html>
                            <html>
                            <head>
                                <meta charset='UTF-8'>
                            </head>
                            <body contenteditable='true'>
                                Lorem ipsum dolor sit amet, consectetur adipiscing elit.
                                Quisque volutpat velit dui, in blandit odio gravida sit amet. Aenean interdum, 
                                sapien sed rutrum imperdiet, leo nisi bibendum felis, et dictum lorem est quis justo. 
                                Donec a iaculis est. Vivamus vel est sit amet dui pulvinar fermentum. Fusce semper augue leo, 
                                id ultricies ante blandit quis. Sed mollis ullamcorper quam vel pharetra. Sed sed eleifend ex, 
                                eu auctor eros. Mauris dapibus, purus eu venenatis pulvinar, turpis turpis congue erat, ut accumsan dui dolor id erat. 
                                Pellentesque non tristique nulla. Vestibulum eget pulvinar ex, non vulputate ante. Nam sodales tristique molestie. 
                                Vivamus ac sapien porttitor, dapibus dui quis, mollis turpis. Curabitur pulvinar vulputate orci. Duis auctor enim a eros egestas, 
                                in ultricies turpis tristique. Praesent congue efficitur nisl, sed laoreet dolor laoreet nec.
                            </body>
                            </html>";
            LogAction("NavigateToString called");
            MyWebView.NavigateToString(html);
        }

        private void SetupActionLogging() 
        {
            if (MyWebView.CoreWebView2 == null) return;

            // Listen for messages from JavaScript
            MyWebView.CoreWebView2.WebMessageReceived += (s, e) =>
            {
                LogAction($"JS Event: {e.TryGetWebMessageAsString()}");
            };

            MyWebView.CoreWebView2.DOMContentLoaded += async (s, e) =>
            {
                string trackingScript = @"
                    (function() {
                        // Track mouse clicks
                        document.addEventListener('click', function(e) {
                            window.chrome.webview.postMessage('CLICK: ' + e.target.tagName + ' at (' + e.clientX + ',' + e.clientY + ')');
                        });

                        // Track key presses
                        document.addEventListener('keydown', function(e) {
                            window.chrome.webview.postMessage('KEYDOWN: ' + e.key + ' (code: ' + e.code + ')');
                        });

                        // Track focus events
                        document.addEventListener('focus', function(e) {
                            window.chrome.webview.postMessage('FOCUS: ' + e.target.tagName);
                        }, true);

                        // Track blur events
                        document.addEventListener('blur', function(e) {
                            window.chrome.webview.postMessage('BLUR: ' + e.target.tagName);
                        }, true);

                        // Track selection changes
                        document.addEventListener('selectionchange', function() {
                            var selection = window.getSelection();
                            if (selection.toString().length > 0) {
                                window.chrome.webview.postMessage('SELECTION: ' + selection.toString().substring(0, 50));
                            }
                        });

                        // Track scroll events (throttled)
                        let scrollTimeout;
                        document.addEventListener('scroll', function(e) {
                            clearTimeout(scrollTimeout);
                            scrollTimeout = setTimeout(function() {
                                window.chrome.webview.postMessage('SCROLL: scrollY=' + window.scrollY);
                            }, 200);
                        }, true);

                        // Track input events for contenteditable
                        document.addEventListener('input', function(e) {
                            window.chrome.webview.postMessage('INPUT: ' + e.target.tagName + ' - ' + e.inputType);
                        });

                        window.chrome.webview.postMessage('Action tracking initialized');
                    })();
                ";

                await MyWebView.CoreWebView2.ExecuteScriptAsync(trackingScript);
                LogAction("Action tracking script injected");
            };
        }

        private void MyWebView_CoreWebView2Initialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
        {
            if (args.Exception != null)
            {
                LogAction($"ERROR: CoreWebView2Initialized with exception: {args.Exception.Message}");
            }
            else
            {
                LogAction($"CoreWebView2Initialized successfully. Browser version: {sender.CoreWebView2?.Environment?.BrowserVersionString}");
            }
        }

        private void WebView2DiagnosticEvents()
        {
            if (MyWebView.CoreWebView2 == null)
            {
                LogAction("WARNING: CoreWebView2 is null, cannot setup diagnostic events");
                return;
            }

            var coreWebView = MyWebView.CoreWebView2;

            coreWebView.ProcessFailed += (s, e) =>
            {
                LogAction($"PROCESS FAILED - Kind: {e.ProcessFailedKind}, Reason: {e.Reason}, Exit code: {e.ExitCode}, Process: {e.ProcessDescription}");
            };

            // Navigation events
            coreWebView.NavigationStarting += (s, e) =>
            {
                LogAction($"NAV Starting: {e.Uri}");
            };

            coreWebView.NavigationCompleted += (s, e) =>
            {
                LogAction($"NAV Completed - Success: {e.IsSuccess}, Status: {e.WebErrorStatus}");
            };

            // Content loading events
            coreWebView.ContentLoading += (s, e) =>
            {
                LogAction("Content loading");
            };

            coreWebView.DOMContentLoaded += (s, e) =>
            {
                LogAction("DOM content loaded");
            };

            LogAction("Diagnostic events setup complete");
        }

        private void Click_Click(object sender, RoutedEventArgs e)
        {
            LogAction("WinUI Button clicked");
            if ((ClickButton.Background as SolidColorBrush)?.Color == Colors.LightBlue)
            {
                ClickButton.Background = new SolidColorBrush(Colors.LightGreen);
            }
            else
            {
                ClickButton.Background = new SolidColorBrush(Colors.LightBlue);
            }
        }
    }
}
