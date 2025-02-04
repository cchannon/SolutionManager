using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Windows.Storage.Pickers;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Windows.Storage;
using Microsoft.Identity.Client;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk.Query;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Http;

namespace SolutionManager
{
    public sealed partial class MainWindow : Window
    {
        private static IConfidentialClientApplication _msalClient;
        private static string _clientId = ""; // Replace with your client ID
        private static string _clientSecret = ""; // Replace with your client secret
        private static string _tenantId = ""; // Replace with your tenant ID
        private static string[] _scopes = ["https://graph.microsoft.com/.default"];

        List<AuthProfile> authProfiles = new();
        List<EnvironmentProfile> environmentProfiles = new();
        List<SolutionProfile> solutionProfiles = new();
        ObservableCollection<RunningJob> jobs = new();
        ObservableCollection<string> matchingSettings = new();
        private Queue<RunningJob> jobQueue = new();
        object settingsObject;
        string userToken = null;
        bool noEnvironmentsFound = false;

        public MainWindow()
        {
            this.InitializeComponent();
            jobsListBox.ItemsSource = jobs;
            matchingConfigsListBox.ItemsSource = matchingSettings;
            matchingUploadConfigsListBox.ItemsSource = matchingSettings;
            environmentList.ItemsSource = environmentProfiles;
            importEnvironmentList.ItemsSource = environmentProfiles;

            // Set default file paths for config CSV files
            string binDirectory = AppContext.BaseDirectory;
        }

        private DoubleAnimation CreateFadeAnimation()
        {
            //just a simple helper to clean up the Grid_Loaded method
            return new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromSeconds(3))
            };
        }

        public async void Grid_Loaded(object sender, RoutedEventArgs e)
        {
            InitializeMsalClient();
            var authResult = await SignInUserAsync();
            if (authResult != null)
            {
                userToken = authResult.AccessToken;
                await InitializeAuthProfilesAsync();
                //_ = InitializeAuthProfilesWithDiscoAsync();
            }

            var storyboard = new Storyboard();

            var fadeAnimation = CreateFadeAnimation();
            Storyboard.SetTarget(fadeAnimation, exportContent);
            Storyboard.SetTargetProperty(fadeAnimation, "Opacity");
            storyboard.Children.Add(fadeAnimation);

            var fadeAnimation2 = CreateFadeAnimation();
            Storyboard.SetTarget(fadeAnimation2, commandPanel);
            Storyboard.SetTargetProperty(fadeAnimation2, "Opacity");
            storyboard.Children.Add(fadeAnimation2);

            var fadeAnimation3 = CreateFadeAnimation();
            Storyboard.SetTarget(fadeAnimation3, pivotFooter);
            Storyboard.SetTargetProperty(fadeAnimation3, "Opacity");
            storyboard.Children.Add(fadeAnimation3);

            storyboard.Begin();
        }

        #region Async Job Handlers
        private async Task InitializeAuthProfilesAsync()
        {
            progressRingOverlay.Visibility = Visibility.Visible;

            string? output = await RunPowerShellScriptAsync("pac auth list");
            if (!string.IsNullOrEmpty(output))
            {
                // Store the authProfiles list for later use
                authProfiles = ParseAuthProfiles(output);

                // Set the currently selected Auth Profile
                var activeProfile = authProfiles.FirstOrDefault(p => p.Active);
                if (activeProfile != null)
                {
                    authProfileText.Text = $"Auth Profile: {activeProfile.Name}";
                }

                // Retrieve the list of environments
                await RetrieveEnvironmentProfilesAsync();
            }
        }

        private async Task RetrieveEnvironmentProfilesAsync()
        {
            progressRingOverlay.Visibility = Visibility.Visible;
            string? output = await RunPowerShellScriptAsync("pac env list");
            if (!string.IsNullOrEmpty(output) && output.IndexOf("not have permission") == -1)
            {
                environmentProfiles = ParseEnvironmentProfiles(output);
            }
            else if (!string.IsNullOrEmpty(output) && output.IndexOf("not have permission") != -1)
            {
                noEnvironmentsFound = true;
                addEnvironmentButton.Visibility = Visibility.Visible;
                if (!CsvFileHasRows())
                {
                    await ShowManualEnvironmentEntryDialog();
                }
            }
            progressRingOverlay.Visibility = Visibility.Collapsed;
        }

        private async Task ShowManualEnvironmentEntryDialog()
        {
            var result = await manualEnvironmentDialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                string environmentUrl = manualEnvironmentUrlTextBox.Text;
                if (!string.IsNullOrEmpty(environmentUrl))
                {
                    if (EnvironmentExistsInCsv(environmentUrl))
                    {
                        var job = new RunningJob
                        {
                            Name = $"Retrieve Environment Info: {environmentUrl}",
                            Status = "Successful",
                            Timestamp = DateTime.Now,
                            Output = "Environment information already exists in CSV. Skipping 'pac env who' command."
                        };

                        StartJob(job);
                    }
                    else
                    {
                        var job = new RunningJob
                        {
                            Name = $"Retrieve Environment Info: {environmentUrl}",
                            Status = "Waiting",
                            Timestamp = DateTime.Now,
                            JobLogic = async (currentJob) =>
                            {
                                string command = $"pac env who -env {environmentUrl}";
                                string? output = await RunPowerShellScriptAsync(command);
                                if (!string.IsNullOrEmpty(output))
                                {
                                    var environmentProfile = ParseEnvironmentInfo(output);
                                    if (environmentProfile != null)
                                    {
                                        environmentProfiles.Add(environmentProfile);
                                        DispatcherQueue.TryEnqueue(() =>
                                        {
                                            environmentList.ItemsSource = null;
                                            environmentList.ItemsSource = environmentProfiles;
                                            importEnvironmentList.ItemsSource = null;
                                            importEnvironmentList.ItemsSource = environmentProfiles;
                                        });
                                        SaveEnvironmentToCsv(environmentProfile); // Save to CSV
                                        currentJob.Status = "Successful";
                                        currentJob.Output = output;
                                    }
                                    else
                                    {
                                        currentJob.Status = "Failed";
                                        currentJob.Output = output;
                                    }
                                }
                                else
                                {
                                    currentJob.Status = "Failed";
                                    currentJob.Output = output;
                                }
                            }
                        };

                        StartJob(job);
                    }
                }
            }
        }

        private async Task InitializeAuthProfilesWithDiscoAsync()
        {
            progressRingOverlay.Visibility = Visibility.Visible;

            try
            {
                var environments = await GetEnvironmentsWithDiscoAsync();
                if (environments != null)
                {
                    environmentProfiles = environments;

                    var activeProfile = environmentProfiles.FirstOrDefault();
                    if (activeProfile != null)
                    {
                        authProfileText.Text = $"Auth Profile: {activeProfile.DisplayName}";
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error: {ex.Message}");
            }
            finally
            {
                progressRingOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private async Task<List<EnvironmentProfile>> GetEnvironmentsWithDiscoAsync()
        {
            var environments = new List<EnvironmentProfile>();

            try
            {
                var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(scheme: "Bearer", userToken);

                var response = await httpClient.GetFromJsonAsync<GlobalDiscoveryResponse>("https://globaldisco.crm.dynamics.com/api/discovery/v2.0/Instances");

                if (response != null && response.value != null)
                {
                    foreach (var instance in response.value)
                    {
                        //environments.Add(new EnvironmentProfile
                        //{
                        //    DisplayName = instance.FriendlyName,
                        //    EnvironmentId = instance.EnvironmentId,
                        //    EnvironmentUrl = instance.ApiUrl,
                        //    UniqueName = instance.UniqueName,
                        //    Active = instance.State == "Ready"
                        //});
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error retrieving environments: {ex.Message}");
            }

            return environments;
        }

        private async Task<string?> RunPowerShellScriptAsync(string scriptText, string workingDirectory = "")
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{scriptText}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory
                };

                using Process process = new();
                process.StartInfo = startInfo;
                process.Start();

                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync();

                if (!string.IsNullOrEmpty(error))
                {
                    Debug.WriteLine($"Error: {error}");
                    return null;
                }

                return output;
            }
            catch (Exception ex)
            {
                return null;
            }
        }
        #endregion

        #region pac return parsing
        private List<AuthProfile> ParseAuthProfiles(string output)
        {
            var authProfiles = new List<AuthProfile>();
            var lines = output.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);

            // Regular expression to match the columns
            var regex = new Regex(@"\[(\d+)\]\s+(\*?)\s+(\S+)\s+(\S+)\s+(\S+)\s+(\S+)\s+(\S*)\s*(.+?)?\s*(https?://\S+)?");

            foreach (var line in lines.Skip(1)) // Skip the header lines
            {
                var match = regex.Match(line);
                if (match.Success)
                {
                    var authProfile = new AuthProfile
                    {
                        Index = int.Parse(match.Groups[1].Value),
                        Active = match.Groups[2].Value == "*",
                        Kind = match.Groups[3].Value,
                        Name = match.Groups[4].Value,
                        User = match.Groups[5].Value,
                        Cloud = match.Groups[6].Value,
                        Type = match.Groups[7].Value,
                        Environment = match.Groups[8].Success ? match.Groups[8].Value : string.Empty,
                        EnvironmentUrl = match.Groups[9].Success ? match.Groups[9].Value : string.Empty
                    };
                    authProfiles.Add(authProfile);
                }
                else
                {
                    // Debugging output to verify the line being parsed
                    Debug.WriteLine($"Failed to parse line: {line}");
                }
            }

            return authProfiles;
        }

        private List<EnvironmentProfile> ParseEnvironmentProfiles(string output)
        {
            var environmentProfiles = new List<EnvironmentProfile>();
            var lines = output.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);

            // Regular expression to match the columns
            var regex = new Regex(@"(\*?)\s+(.+?)\s+([a-f0-9-]{36})\s+(https?://\S+)\s+(\S+)");

            foreach (var line in lines.Skip(2)) // Skip the header lines
            {
                var match = regex.Match(line);
                if (match.Success)
                {
                    var environmentProfile = new EnvironmentProfile
                    {
                        Active = match.Groups[1].Value == "*",
                        DisplayName = match.Groups[2].Value.Trim(),
                        EnvironmentId = match.Groups[3].Value,
                        EnvironmentUrl = match.Groups[4].Value,
                        UniqueName = match.Groups[5].Value
                    };
                    environmentProfiles.Add(environmentProfile);
                }
                else
                {
                    // Debugging output to verify the line being parsed
                    Debug.WriteLine($"Failed to parse line: {line}");
                }
            }

            return environmentProfiles;
        }

        private EnvironmentProfile? ParseEnvironmentInfo(string output)
        {
            try
            {
                var lines = output.Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries);
                var environmentProfile = new EnvironmentProfile()
                {
                    DisplayName = string.Empty,
                    EnvironmentId = string.Empty,
                    EnvironmentUrl = string.Empty,
                    UniqueName = string.Empty
                };

                foreach (var line in lines)
                {
                    if (line.IndexOf("Org ID:") != -1)
                    {
                        environmentProfile.EnvironmentId = line.Split(':')[1].Trim();
                    }
                    else if (line.IndexOf("Unique Name:") != -1)
                    {
                        environmentProfile.UniqueName = line.Split(':')[1].Trim();
                    }
                    else if (line.IndexOf("Friendly Name:")!= -1)
                    {
                        environmentProfile.DisplayName = line.Split(':')[1].Trim();
                    }
                    else if (line.IndexOf("Org URL:") != -1)
                    {
                        environmentProfile.EnvironmentUrl = line.Split(':')[1].Trim() + ":" + line.Split(':')[2].Trim();
                    }
                }

                if (!string.IsNullOrEmpty(environmentProfile.EnvironmentId) &&
                    !string.IsNullOrEmpty(environmentProfile.UniqueName) &&
                    !string.IsNullOrEmpty(environmentProfile.DisplayName) &&
                    !string.IsNullOrEmpty(environmentProfile.EnvironmentUrl))
                {
                    return environmentProfile;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error parsing environment info: {ex.Message}");
            }

            return null;
        }

        private List<SolutionProfile> ParseSolutionProfiles(string output)
        {
            var solutionProfiles = new List<SolutionProfile>();
            var lines = output.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);

            // Regular expression to match the columns
            var regex = new Regex(@"^(\S+)\s+(.+?)\s+(\S+)\s+(True|False)$", RegexOptions.Multiline);

            foreach (var line in lines.Skip(4)) // Skip the header lines
            {
                var match = regex.Match(line);
                if (match.Success)
                {
                    if (!bool.Parse(match.Groups[4].Value))
                    {
                        var solutionProfile = new SolutionProfile
                        {
                            UniqueName = match.Groups[1].Value,
                            FriendlyName = match.Groups[2].Value.Trim(),
                            Version = match.Groups[3].Value,
                        };
                        solutionProfiles.Add(solutionProfile);
                    }
                }
                else
                {
                    // Debugging output to verify the line being parsed
                    Debug.WriteLine($"Failed to parse line: {line}");
                }
            }

            return solutionProfiles;
        }
        #endregion

        #region Queue and Job Handling
        private void EnqueueJob(RunningJob job)
        {
            job.Status = "Waiting";
            jobQueue.Enqueue(job);
            jobs.Add(job);
            jobsPanel.Visibility = Visibility.Visible;
            TryStartNextJob();
        }

        private async void TryStartNextJob()
        {
            if(jobQueue.Count == 0)
            {
                return;
            }

            var nextJob = jobQueue.Peek();

            // Check if the predecessor job has failed
            if (nextJob.PredecessorId != null)
            {
                var predecessorJob = jobs.FirstOrDefault(j => j.Id == nextJob.PredecessorId);
                if (predecessorJob != null && predecessorJob.Status == "Failed")
                {
                    nextJob.Status = "Failed";
                    nextJob.Output = "Predecessor job failed. This job will not run.";
                    jobQueue.Dequeue();
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        jobsListBox.ItemsSource = null;
                        jobsListBox.ItemsSource = jobs;
                    });
                    TryStartNextJob();
                    return;
                }
                else if (predecessorJob != null && (predecessorJob.Status == "Waiting" || predecessorJob.Status == "In Progress"))
                {
                    // Move the job to the end of the queue
                    jobQueue.Dequeue();
                    jobQueue.Enqueue(nextJob);
                    // Wait 1 second before retry to limit the tax on the system
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    TryStartNextJob();
                    return;
                }
            }

            nextJob = jobQueue.Dequeue();
            nextJob.Status = "In Progress";

            DispatcherQueue.TryEnqueue(() =>
            {
                jobsListBox.ItemsSource = null;
                jobsListBox.ItemsSource = jobs;
            });

            await Task.Run(async () =>
            {
                try
                {
                    if (nextJob.JobLogic != null)
                    {
                        await nextJob.JobLogic(nextJob);
                    }
                    else
                    {
                        throw new InvalidOperationException("Job logic is not defined.");
                    }
                }
                catch (Exception ex)
                {
                    nextJob.Status = "Failed";
                    nextJob.Output = $"Error: {ex.Message}";
                }
                finally
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        jobsListBox.ItemsSource = null;
                        jobsListBox.ItemsSource = jobs;
                    });
                    TryStartNextJob();
                }
            });
        }

        private void StartJob(RunningJob job)
        {
            job.Status = "In Progress";
            jobs.Add(job);
            jobsPanel.Visibility = Visibility.Visible;

            _ = Task.Run(async () =>
            {
                try
                {
                    if (job.JobLogic != null)
                    {
                        await job.JobLogic(job);
                    }
                    else
                    {
                        throw new InvalidOperationException("Job logic is not defined.");
                    }
                }
                catch (Exception ex)
                {
                    job.Status = "Failed";
                    job.Output = $"Error: {ex.Message}";
                }
                finally
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        jobsListBox.ItemsSource = null;
                        jobsListBox.ItemsSource = jobs;
                    });
                }
            });
        }
        #endregion

        #region Event Handlers

        private async void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.Desktop;
            picker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                if (sender is Button button)
                {
                    switch (button.Name)
                    {
                        case "exportPathBrowse":
                            zipFilePathTextBox.Text = folder.Path;
                            break;
                    }
                }
            }
        }

        private async void AddEnvironmentButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowManualEnvironmentEntryDialog();
        }

        private async void BrowseFileButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.Desktop
            };

            var fileTypeFilters = new Dictionary<string, string>
            {
                { "importZipFileBrowse", ".zip" },
                { "importJsonFileBrowse", ".json" },
                { "settingsSolutionPathBrowse", ".zip" }
            };

            if (sender is Button button && fileTypeFilters.TryGetValue(button.Name, out string? fileType))
            {
                picker.FileTypeFilter.Add(fileType);

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    switch (button.Name)
                    {
                        case "importZipFileBrowse":
                            importZipPathTextBox.Text = file.Path;
                            settingsSolutionZipTextBox.Text = file.Path;
                            CheckStoredSettings(file.Path);
                            break;
                        case "importJsonFileBrowse":
                            importJsonPathTextBox.Text = file.Path;
                            break;
                        case "settingsSolutionPathBrowse":
                            settingsSolutionZipTextBox.Text = file.Path;
                            importZipPathTextBox.Text = file.Path;
                            CheckStoredSettings(file.Path);
                            break;
                    }
                }
            }
        }

        private async void AddSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.Desktop
            };
            picker.FileTypeFilter.Add(".json");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                string jsonContent = await FileIO.ReadTextAsync(file);

                // Prompt the user to enter a target environment name
                var dialog = new ContentDialog
                {
                    Title = "Select Target Environment",
                    PrimaryButtonText = "OK",
                    CloseButtonText = "Cancel",
                    XamlRoot = this.Content.XamlRoot
                };

                var listBox = new ListBox
                {                    
                    ItemsSource = environmentProfiles,
                    DisplayMemberPath = "DisplayName"
                };

                var scrollViewer = new ScrollViewer
                {
                    Content = listBox,
                    MaxHeight = 300 // Set a maximum height for the ScrollViewer
                };

                dialog.Content = scrollViewer;

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    var selectedEnvironment = (EnvironmentProfile)listBox.SelectedItem;
                    if (selectedEnvironment == null)
                    {
                        // Handle the case where no environment is selected
                        settingsLogTextBlock.Text += "Target environment not selected." + Environment.NewLine;
                        return;
                    }

                    string targetEnvironment = selectedEnvironment.DisplayName;

                    try
                    {
                        string settingsFilePath = GetSettingsFilePath(settingsSolutionZipTextBox.Text);

                        if (File.Exists(settingsFilePath))
                        {
                            string existingJsonContent = File.ReadAllText(settingsFilePath);
                            using JsonDocument document = JsonDocument.Parse(existingJsonContent);
                            JsonElement root = document.RootElement;

                            var newEnvironment = new Dictionary<string, JsonElement>
                            {
                                { targetEnvironment, JsonDocument.Parse(jsonContent).RootElement }
                            };

                            if (root.TryGetProperty("Environments", out JsonElement environments))
                            {
                                var mergedEnvironments = new List<JsonElement>(environments.EnumerateArray())
                                {
                                    // Add the new environment
                                    JsonDocument.Parse(JsonSerializer.Serialize(newEnvironment)).RootElement
                                };

                                var mergedJson = new
                                {
                                    Environments = mergedEnvironments
                                };

                                string mergedJsonContent = JsonSerializer.Serialize(mergedJson, new JsonSerializerOptions { WriteIndented = true });
                                File.WriteAllText(settingsFilePath, mergedJsonContent);
                            }
                        }
                        else
                        {
                            var newEnvironment = new Dictionary<string, JsonElement>
                            {
                                { targetEnvironment, JsonDocument.Parse(jsonContent).RootElement }
                            };

                            var newJson = new
                            {
                                Environments = new[] { newEnvironment }
                            };

                            string newJsonContent = JsonSerializer.Serialize(newJson, new JsonSerializerOptions { WriteIndented = true });
                            File.WriteAllText(settingsFilePath, newJsonContent);
                        }

                        string message = $"Settings file {settingsFilePath} updated.";
                        settingsLogTextBlock.Text = message + Environment.NewLine;

                        CheckStoredSettings(settingsSolutionZipTextBox.Text);
                    }
                    catch (Exception ex)
                    {
                        string message = $"Error: {ex.Message}";
                        settingsLogTextBlock.Text = message + Environment.NewLine;
                    }
                }
            }
        }

        private async void GenerateSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.Desktop;
            picker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();

            string command = $"pac solution create-settings -z '{settingsSolutionZipTextBox.Text}' -s '{Path.Combine(folder.Path,  Path.GetFileNameWithoutExtension(settingsSolutionZipTextBox.Text) +"settings.json")}'";

            RunningJob job = new()
            {
                Name = $"Generate Settings: {Path.GetFileName(settingsSolutionZipTextBox.Text)}",
                Status = "In Progress",
                Timestamp = DateTime.Now,
                JobLogic = async (currentJob) =>
                {
                    string? output = await RunPowerShellScriptAsync(command);
                    currentJob.Output = output ?? string.Empty;
                    currentJob.Status = !string.IsNullOrEmpty(output) && output.Contains("created", StringComparison.OrdinalIgnoreCase) ? "Successful" : "Failed";
                }
            };

            StartJob(job);
        }

        private async void ValidateSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            settingsLogTextBlock.Text = "Validating settings files..." + Environment.NewLine + Environment.NewLine;
            try
            {
                string settingsFilePath = GetSettingsFilePath(settingsSolutionZipTextBox.Text);

                if (!File.Exists(settingsFilePath))
                {
                    settingsLogTextBlock.Text = "Settings file not found." + Environment.NewLine;
                    return;
                }

                // Generate a new settings file based on the provided zip file
                string tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempDirectory);
                string newSettingsFilePath = Path.Combine(tempDirectory, "newSettings.json");

                string command = $"pac solution create-settings -z '{settingsSolutionZipTextBox.Text}' -s '{newSettingsFilePath}'";
                string? output = await RunPowerShellScriptAsync(command);

                if (string.IsNullOrEmpty(output) || !File.Exists(newSettingsFilePath))
                {
                    settingsLogTextBlock.Text = "Failed to generate new settings file." + Environment.NewLine;
                    return;
                }

                string newSettingsContent = File.ReadAllText(newSettingsFilePath);
                using JsonDocument newDocument = JsonDocument.Parse(newSettingsContent);
                JsonElement newSettings = newDocument.RootElement;

                string existingJsonContent = File.ReadAllText(settingsFilePath);
                using JsonDocument existingDocument = JsonDocument.Parse(existingJsonContent);
                JsonElement existingRoot = existingDocument.RootElement;

                if (existingRoot.TryGetProperty("Environments", out JsonElement environments))
                {
                    foreach (JsonElement env in environments.EnumerateArray())
                    {
                        foreach (JsonProperty envProperty in env.EnumerateObject())
                        {
                            string environmentName = envProperty.Name;
                            JsonElement storedSettings = envProperty.Value;
                            
                            ValidateEnvironmentSettings(environmentName, newSettings, storedSettings);
                        }
                    }
                }

                string message = $"Settings Validation Complete";
                settingsLogTextBlock.Text += message + Environment.NewLine;
                Directory.Delete(tempDirectory, true);
            }
            catch (Exception ex)
            {
                string message = $"Error: {ex.Message}";
                settingsLogTextBlock.Text += message + Environment.NewLine;
            }
        }

        private void RemoveSelectedSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = matchingConfigsListBox.SelectedItems.Cast<string>().ToList();
            foreach (var item in selectedItems)
            {
                matchingSettings.Remove(item);
            }

            string settingsFilePath = GetSettingsFilePath(settingsSolutionZipTextBox.Text);
            if (File.Exists(settingsFilePath))
            {
                string jsonContent = File.ReadAllText(settingsFilePath);
                using JsonDocument document = JsonDocument.Parse(jsonContent);
                JsonElement root = document.RootElement;

                if (root.TryGetProperty("Environments", out JsonElement environments))
                {
                    var updatedEnvironments = new List<JsonElement>();
                    foreach (JsonElement env in environments.EnumerateArray())
                    {
                        var updatedEnv = new Dictionary<string, JsonElement>();
                        foreach (JsonProperty envProperty in env.EnumerateObject())
                        {
                            if (!selectedItems.Contains(envProperty.Name))
                            {
                                updatedEnv.Add(envProperty.Name, envProperty.Value);
                            }
                        }
                        if (updatedEnv.Count > 0)
                        {
                            updatedEnvironments.Add(JsonDocument.Parse(JsonSerializer.Serialize(updatedEnv)).RootElement);
                        }
                    }

                    var updatedJson = new
                    {
                        Environments = updatedEnvironments
                    };

                    string updatedJsonContent = JsonSerializer.Serialize(updatedJson, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(settingsFilePath, updatedJsonContent);

                    settingsLogTextBlock.Text = $"Removed selected settings from {settingsFilePath}." + Environment.NewLine;
                }
            }
        }

        private async void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (environmentList.SelectedItem is EnvironmentProfile selectedEnvironment &&
                solutionsList.SelectedItem is SolutionProfile selectedSolution)
            {
                string preVersionJobId = "";
                if (incrementSolutionToggle.IsOn)
                {
                    var serviceClient = GetServiceClient(selectedEnvironment.EnvironmentUrl);
                    var preVersionJob = new RunningJob
                    {
                        Name = $"Version Increment in Dataverse: {selectedSolution.FriendlyName}",
                        Status = "Waiting",
                        Timestamp = DateTime.Now,
                        Environment = selectedEnvironment.EnvironmentId,
                        JobLogic = async (currentJob) =>
                        {
                            var query = new QueryExpression("solution")
                            {
                                ColumnSet = new ColumnSet("version"),
                                Criteria = new FilterExpression
                                {
                                    Conditions =
                                {
                                    new ConditionExpression("uniquename", ConditionOperator.Equal, selectedSolution.UniqueName)
                                }
                                }
                            };

                            var solutions = serviceClient.RetrieveMultiple(query).Entities;
                            if (solutions.Count > 0)
                            {
                                var solution = solutions.First();
                                var version = solution.GetAttributeValue<string>("version");

                                var versionParts = version.Split('.');

                                DispatcherQueue.TryEnqueue(() =>
                                {
                                    if (customStrategyRadioButton.IsChecked ?? false)
                                    {
                                        if (!string.IsNullOrEmpty(releaseVer.Text))
                                        {
                                            versionParts[2] = string.IsNullOrEmpty(releaseVer.Text) ? "0" : releaseVer.Text;
                                        }
                                        if (!string.IsNullOrEmpty(buildVer.Text))
                                        {
                                            versionParts[3] = string.IsNullOrEmpty(buildVer.Text) ? "0" : buildVer.Text;
                                        }
                                    }
                                    else if (solutionStrategyRadioButton.IsChecked ?? false)
                                    {
                                        versionParts[2] = (int.Parse(versionParts[2]) + 1).ToString();
                                        versionParts[3] = "0";
                                    }

                                    var newVersion = string.Join(".", versionParts);
                                    solution["version"] = newVersion;
                                    serviceClient.Update(solution);

                                    currentJob.Status = "Successful";
                                    currentJob.Output = $"Version incremented in Dataverse to {newVersion}.";
                                });
                            }
                            else
                            {
                                currentJob.Status = "Failed";
                                currentJob.Output = "Solution not found in Dataverse.";
                            }
                        }
                    };
                    preVersionJobId = preVersionJob.Id;

                    // Check for existing jobs with the same environment
                    var existingJob = jobs
                        .Where(j => j.Environment == selectedEnvironment.EnvironmentId && (j.Status == "In Progress" || j.Status == "Waiting"))
                        .OrderByDescending(j => j.Timestamp)
                        .FirstOrDefault();

                    if (existingJob != null)
                    {
                        preVersionJob.PredecessorId = existingJob.Id;
                        EnqueueJob(preVersionJob);
                    }
                    else
                    {
                        StartJob(preVersionJob);
                    }
                }

                var zipFilePath = zipFilePathTextBox.Text;
                var exportAsManaged = exportAsManagedCheckBox.IsChecked ?? false;
                var overwrite = overwriteCheckBox.IsChecked ?? false;
                var includeSettings = new List<string>();

                foreach (ListBoxItem item in includeSettingsListBox.SelectedItems)
                {
                    includeSettings.Add(item.Content.ToString());
                }

                // Construct the pac solution export command
                var command = $"pac solution export --environment {selectedEnvironment.EnvironmentUrl} --name {selectedSolution.UniqueName} --path '{zipFilePath}'";

                if (exportAsManaged)
                {
                    command += " --managed";
                }

                if (overwrite)
                {
                    command += " --overwrite";
                }

                if (includeSettings.Any())
                {
                    command += $" --include {string.Join(",", includeSettings)}";
                }

                // Create a new job and add it to the jobs list
                var job = new RunningJob
                {
                    Name = $"Export: {selectedSolution.FriendlyName}",
                    Status = "Waiting",
                    Timestamp = DateTime.Now,
                    Environment = selectedEnvironment.EnvironmentId,
                    JobLogic = async (currentJob) =>
                    {
                        string? output = await RunPowerShellScriptAsync(command);
                        if (string.IsNullOrEmpty(output) || !output.Contains("succeeded", StringComparison.OrdinalIgnoreCase))
                        {
                            currentJob.Status = "Failed";
                            currentJob.Output = output ?? string.Empty;
                            return;
                        }

                        currentJob.Status = "Successful";
                        currentJob.Output = output ?? string.Empty;
                    }
                };

                if(incrementSolutionToggle.IsOn)
                {
                    job.PredecessorId = preVersionJobId;
                    EnqueueJob(job);
                }
                else
                {
                    // Check for existing jobs with the same environment
                    var existingJob = jobs
                        .Where(j => j.Environment == selectedEnvironment.EnvironmentId && (j.Status == "In Progress" || j.Status == "Waiting"))
                        .OrderByDescending(j => j.Timestamp)
                        .FirstOrDefault();

                    if (existingJob != null)
                    {
                        job.PredecessorId = existingJob.Id;
                        EnqueueJob(job);
                    }
                    else
                    {
                        StartJob(job);
                    }
                }

                string vjobid = "";

                if (!(noStrategyRadioButton.IsChecked ?? false) && !incrementSolutionToggle.IsOn)
                {
                    var versionJob = new RunningJob
                    {
                        Name = $"Version Increment: {selectedSolution.FriendlyName}",
                        Status = "Waiting",
                        Timestamp = DateTime.Now,
                        Environment = selectedEnvironment.EnvironmentId,
                        PredecessorId = job.Id,
                        JobLogic = async (currentJob) =>
                        {
                            // Extract solution.xml from the zip file
                            string tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                            Directory.CreateDirectory(tempDirectory);
                            string solutionXmlPath = Path.Combine(tempDirectory, "solution.xml");

                            using (ZipArchive archive = ZipFile.OpenRead(zipFilePath))
                            {
                                var solutionEntry = archive.GetEntry("solution.xml");
                                if (solutionEntry != null)
                                {
                                    solutionEntry.ExtractToFile(solutionXmlPath, true);
                                }
                                else
                                {
                                    currentJob.Status = "Failed";
                                    currentJob.Output += Environment.NewLine + "solution.xml not found in the zip file.";
                                    return;
                                }
                            }

                            // Run the pac solution version command on the extracted solution.xml
                            var command = $"pac solution version -sp '{solutionXmlPath}'";
                            if (customStrategyRadioButton.IsChecked ?? false)
                            {
                                if (!string.IsNullOrEmpty(releaseVer.Text))
                                {
                                    command += $" --revisionversion {releaseVer.Text}";
                                }
                                if (!string.IsNullOrEmpty(buildVer.Text))
                                {
                                    command += $" --buildversion {buildVer.Text}";
                                }
                            }
                            else if (solutionStrategyRadioButton.IsChecked ?? false)
                            {
                                command += " -s solution";
                            }
                            string? output = await RunPowerShellScriptAsync(command);
                            if (string.IsNullOrEmpty(output) || !output.Contains("succeeded", StringComparison.OrdinalIgnoreCase))
                            {
                                currentJob.Status = "Failed";
                                currentJob.Output += Environment.NewLine + output ?? string.Empty;
                                return;
                            }

                            // Replace the solution.xml in the zip file with the updated version
                            using (ZipArchive archive = ZipFile.Open(zipFilePath, ZipArchiveMode.Update))
                            {
                                var solutionEntry = archive.GetEntry("solution.xml");
                                solutionEntry?.Delete();
                                archive.CreateEntryFromFile(solutionXmlPath, "solution.xml");
                            }

                            // Clean up temporary directory
                            Directory.Delete(tempDirectory, true);

                            currentJob.Status = "Successful";
                            currentJob.Output = output ?? string.Empty;
                        }
                    };
                    vjobid = versionJob.Id;
                    EnqueueJob(versionJob);
                }

                if (CheckInAfterExport.IsChecked == true)
                {
                    var checkinJob = new RunningJob
                    {
                        Name = $"Git Check-in: {selectedSolution.FriendlyName}",
                        Status = "Waiting",
                        Timestamp = DateTime.Now,
                        Environment = selectedEnvironment.EnvironmentId,
                        PredecessorId = (noStrategyRadioButton.IsChecked ?? false || incrementSolutionToggle.IsOn) ? job.Id : vjobid,
                        JobLogic = async (currentJob) =>
                        {
                            try
                            {
                                // Run git add
                                string gitAddCommand = "git add .";
                                string? gitAddOutput = await RunPowerShellScriptAsync(gitAddCommand, zipFilePath);
                                if (string.IsNullOrEmpty(gitAddOutput))
                                {
                                    currentJob.Status = "Failed";
                                    currentJob.Output = "Git add failed.";
                                    return;
                                }

                                // Run git commit
                                string gitCommitCommand = "git commit -m \"Solution exported via Air Traffic Control Tower\"";
                                string? gitCommitOutput = await RunPowerShellScriptAsync(gitCommitCommand, zipFilePath);
                                if (string.IsNullOrEmpty(gitCommitOutput))
                                {
                                    currentJob.Status = "Failed";
                                    currentJob.Output = "Git commit failed.";
                                    return;
                                }

                                // Run git push
                                string gitPushCommand = "git push";
                                string? gitPushOutput = await RunPowerShellScriptAsync(gitPushCommand, zipFilePath);
                                if (string.IsNullOrEmpty(gitPushOutput))
                                {
                                    currentJob.Status = "Failed";
                                    currentJob.Output = "Git push failed.";
                                    return;
                                }

                                currentJob.Status = "Successful";
                                currentJob.Output = "Git add, commit, and push succeeded.";
                            }
                            catch (Exception ex)
                            {
                                currentJob.Status = "Failed";
                                currentJob.Output = $"Error: {ex.Message}";
                            }
                        }
                    };

                    EnqueueJob(checkinJob);
                }
            }
            else
            {
                // Handle the case where no environment or solution is selected
                Debug.WriteLine("No environment or solution selected.");
            }
        }

        private void PublishButton_Click(object sender, RoutedEventArgs e)
        {
            if (environmentList.SelectedItem is EnvironmentProfile selectedEnvironment)
            {
                // Construct the pac solution publish command
                var command = $"pac solution publish --environment {selectedEnvironment.EnvironmentUrl} --async";

                // Create a new job and add it to the jobs list
                var job = new RunningJob
                {
                    Name = $"Publish All: {selectedEnvironment.DisplayName}",
                    Status = "In Progress",
                    Timestamp = DateTime.Now,
                    Environment = selectedEnvironment.EnvironmentId,
                    JobLogic = async (currentJob) =>
                    {
                        string? output = await RunPowerShellScriptAsync(command);
                        currentJob.Output = output ?? string.Empty;
                        currentJob.Status = !string.IsNullOrEmpty(output) && output.Contains("successfully", StringComparison.OrdinalIgnoreCase) ? "Successful" : "Failed";
                    }
                };

                // Check for existing jobs with the same environment
                var existingJob = jobs
                    .Where(j => j.Environment == selectedEnvironment.EnvironmentId && (j.Status == "In Progress" || j.Status == "Waiting"))
                    .OrderByDescending(j => j.Timestamp)
                    .FirstOrDefault();
                if (existingJob != null)
                {
                    job.PredecessorId = existingJob.Id;
                    EnqueueJob(job);
                }
                else
                {
                    StartJob(job);
                }
            }
            else
            {
                // Handle the case where no environment is selected
                Debug.WriteLine("No environment selected.");
            }
        }

        private void ChangeAuthProfileButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "Select Authentication Profile",
                Content = new ListBox
                {
                    ItemsSource = authProfiles,
                    DisplayMemberPath = "DisplayName"
                },
                PrimaryButtonText = "OK",
                CloseButtonText = "Cancel",
                XamlRoot = this.Content.XamlRoot
            };

            dialog.PrimaryButtonClick += async (s, args) =>
            {
                var listBox = (ListBox)dialog.Content;
                var selectedProfile = (AuthProfile)listBox.SelectedItem;
                if (selectedProfile != null)
                {
                    authProfileText.Text = $"Auth Profile: {selectedProfile.Name}";
                    // Set the selected profile as active
                    progressRingOverlay.Visibility = Visibility.Visible;
                    Debug.WriteLine("ProgressRingOverlay set to Visible");
                    await RunPowerShellScriptAsync($"pac auth select --index {selectedProfile.Index}");
                    // Retrieve the list of environments
                    await RetrieveEnvironmentProfilesAsync();
                    progressRingOverlay.Visibility = Visibility.Collapsed;
                    Debug.WriteLine("ProgressRingOverlay set to Collapsed");
                }
            };

            _ = dialog.ShowAsync();
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {

            if (matchingUploadConfigsListBox.SelectedItems.Count > 0)
            {
                foreach (string targetName in matchingUploadConfigsListBox.SelectedItems)
                {
                    var target = environmentList.Items
                        .Cast<EnvironmentProfile>()
                        .FirstOrDefault(env => env.DisplayName == targetName);
                    if (target != null)
                    {
                        var solutionFilePath = importZipPathTextBox.Text;
                        var activatePlugins = activatePluginsCheckBox.IsChecked ?? false;
                        var stageAndUpgrade = stageAndUpgradeCheckBox.IsChecked ?? false;
                        var publishAfterImport = publishAfterImportCheckBox.IsChecked ?? false;
                        var forceOverwrite = forceOverwriteCheckBox.IsChecked ?? false;

                        string settingsFilePath = GetSettingsFilePath(solutionFilePath);
                        string tempJsonFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");

                        try
                        {
                            if (File.Exists(settingsFilePath))
                            {
                                string jsonContent = File.ReadAllText(settingsFilePath);
                                using JsonDocument document = JsonDocument.Parse(jsonContent);
                                JsonElement root = document.RootElement;

                                if (root.TryGetProperty("Environments", out JsonElement environments))
                                {
                                    var targetEnvironment = environments.EnumerateArray()
                                        .SelectMany(env => env.EnumerateObject())
                                        .FirstOrDefault(envProperty => envProperty.Name == targetName);

                                    if (targetEnvironment.Value.ValueKind != JsonValueKind.Undefined)
                                    {
                                        // Write the JSON object to a temporary file
                                        File.WriteAllText(tempJsonFilePath, JsonSerializer.Serialize(targetEnvironment.Value, new JsonSerializerOptions { WriteIndented = true }));
                                    }
                                    else
                                    {
                                        Debug.WriteLine($"Target environment '{targetName}' not found in settings file.");
                                        continue;
                                    }
                                }
                            }

                            // Construct the pac solution import command
                            var command = $"pac solution import --environment {target.EnvironmentUrl} --path '{solutionFilePath}'";

                            if (File.Exists(tempJsonFilePath))
                            {
                                command += $" --settings-file '{tempJsonFilePath}'";
                            }

                            if (activatePlugins)
                            {
                                command += " --activate-plugins";
                            }

                            if (stageAndUpgrade)
                            {
                                command += " --stage-and-upgrade";
                            }

                            if (publishAfterImport)
                            {
                                command += " --publish-changes";
                            }

                            if (forceOverwrite)
                            {
                                command += " --force-overwrite";
                            }

                            // Create a new job and add it to the jobs list
                            var job = new RunningJob
                            {
                                Name = $"Import: {Path.GetFileName(solutionFilePath)}",
                                Status = "Waiting",
                                Timestamp = DateTime.Now,
                                Environment = target.EnvironmentId,
                                JobLogic = async (currentJob) =>
                                {
                                    string? output = await RunPowerShellScriptAsync(command);
                                    currentJob.Output = output ?? string.Empty;
                                    currentJob.Status = !string.IsNullOrEmpty(output) && output.Contains("success", StringComparison.OrdinalIgnoreCase) ? "Successful" : "Failed";

                                    // Delete the temporary JSON file
                                    if (File.Exists(tempJsonFilePath))
                                    {
                                        File.Delete(tempJsonFilePath);
                                    }
                                }
                            };

                            // Check for existing jobs with the same environment
                            var existingJob = jobs
                                .Where(j => j.Environment == target.EnvironmentId && (j.Status == "In Progress" || j.Status == "Waiting"))
                                .OrderByDescending(j => j.Timestamp)
                                .FirstOrDefault();

                            if (existingJob != null)
                            {
                                job.PredecessorId = existingJob.Id;
                                EnqueueJob(job);
                            }
                            else
                            {
                                StartJob(job);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error processing target environment '{targetName}': {ex.Message}");
                            // Ensure the temporary file is deleted in case of an error
                            if (File.Exists(tempJsonFilePath))
                            {
                                File.Delete(tempJsonFilePath);
                            }
                        }
                    }
                }
            }
            else if(importEnvironmentList.SelectedItem != null)
            {
                var solutionFilePath = importZipPathTextBox.Text;
                var activatePlugins = activatePluginsCheckBox.IsChecked ?? false;
                var stageAndUpgrade = stageAndUpgradeCheckBox.IsChecked ?? false;
                var publishAfterImport = publishAfterImportCheckBox.IsChecked ?? false;
                var forceOverwrite = forceOverwriteCheckBox.IsChecked ?? false;

                string settingsFilePath = importJsonPathTextBox.Text;
                if (importEnvironmentList.SelectedItem is EnvironmentProfile selectedEnv)
                {
                    try
                    {

                        // Construct the pac solution import command
                        var command = $"pac solution import --environment {selectedEnv.EnvironmentUrl} --path '{solutionFilePath}'";

                        if (File.Exists(settingsFilePath))
                        {
                            command += $" --settings-file '{settingsFilePath}'";
                        }

                        if (activatePlugins)
                        {
                            command += " --activate-plugins";
                        }

                        if (stageAndUpgrade)
                        {
                            command += " --stage-and-upgrade";
                        }

                        if (publishAfterImport)
                        {
                            command += " --publish-changes";
                        }

                        if (forceOverwrite)
                        {
                            command += " --force-overwrite";
                        }

                        // Create a new job and add it to the jobs list
                        var job = new RunningJob
                        {
                            Name = $"Import: {Path.GetFileName(solutionFilePath)}",
                            Status = "Waiting",
                            Timestamp = DateTime.Now,
                            Environment = selectedEnv.EnvironmentId,
                            JobLogic = async (currentJob) =>
                            {
                                string? output = await RunPowerShellScriptAsync(command);
                                currentJob.Output = output ?? string.Empty;
                                currentJob.Status = !string.IsNullOrEmpty(output) && output.Contains("success", StringComparison.OrdinalIgnoreCase) ? "Successful" : "Failed";
                            }
                        };

                        // Check for existing jobs with the same environment
                        var existingJob = jobs
                            .Where(j => j.Environment == selectedEnv.EnvironmentId && (j.Status == "In Progress" || j.Status == "Waiting"))
                            .OrderByDescending(j => j.Timestamp)
                            .FirstOrDefault();

                        if (existingJob != null)
                        {
                            job.PredecessorId = existingJob.Id;
                            EnqueueJob(job);
                        }
                        else
                        {
                            StartJob(job);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error processing target environment '{selectedEnv}': {ex.Message}");
                    }
                }


            }
            else
            {
                // Handle the case where no environment is selected
                Debug.WriteLine("No environment selected.");
            }
        }

        private void SettingsUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (matchingConfigsListBox.SelectedItem is string selectedConfig)
            {
                string selectedC = selectedConfig.Replace("⚠️ ", string.Empty);
                try
                {
                    // Validate the JSON content
                    string updatedContent = settingsUpdateTextBox.Text;
                    if (!IsValidJson(updatedContent))
                    {
                        ShowErrorDialog("Invalid JSON format.");
                        return;
                    }

                    string settingsFilePath = GetSettingsFilePath(settingsSolutionZipTextBox.Text);

                    if (File.Exists(settingsFilePath))
                    {
                        string jsonContent = File.ReadAllText(settingsFilePath);
                        using JsonDocument document = JsonDocument.Parse(jsonContent);
                        JsonElement root = document.RootElement;

                        if (root.TryGetProperty("Environments", out JsonElement environments))
                        {
                            var environmentsList = environments.EnumerateArray().ToList();
                            for (int i = 0; i < environmentsList.Count; i++)
                            {
                                var env = environmentsList[i];
                                if (env.TryGetProperty(selectedC, out JsonElement envObject))
                                {
                                    var updatedEnvObject = JsonDocument.Parse(settingsUpdateTextBox.Text).RootElement;
                                    var updatedEnv = new Dictionary<string, JsonElement>
                                    {
                                        { selectedC, updatedEnvObject }
                                    };

                                    environmentsList[i] = JsonDocument.Parse(JsonSerializer.Serialize(updatedEnv)).RootElement;
                                    break;
                                }
                            }

                            var updatedJson = new
                            {
                                Environments = environmentsList
                            };

                            string updatedJsonContent = JsonSerializer.Serialize(updatedJson, new JsonSerializerOptions { WriteIndented = true });
                            File.WriteAllText(settingsFilePath, updatedJsonContent);

                            settingsLogTextBlock.Text = $"Settings for {selectedC} updated successfully." + Environment.NewLine;
                            settingsLogTextBlock.Visibility = Visibility.Visible;
                            settingsUpdateCommands.Visibility = Visibility.Collapsed;
                            settingsUpdateTextBox.Visibility = Visibility.Collapsed;
                            matchingConfigsListBox.SelectedItem = null;
                        }
                    }
                }
                catch (Exception ex)
                {
                    settingsLogTextBlock.Text = $"Error: {ex.Message}" + Environment.NewLine;
                }
            }
        }

        private void SettingsUpdateCancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (matchingConfigsListBox.SelectedItem is string selectedConfig)
            {
                string selectedC = selectedConfig.Replace("⚠️ ", string.Empty);
                try
                {
                    string settingsFilePath = GetSettingsFilePath(settingsSolutionZipTextBox.Text);

                    if (File.Exists(settingsFilePath))
                    {
                        string jsonContent = File.ReadAllText(settingsFilePath);
                        using JsonDocument document = JsonDocument.Parse(jsonContent);
                        JsonElement root = document.RootElement;

                        if (root.TryGetProperty("Environments", out JsonElement environments))
                        {
                            foreach (JsonElement env in environments.EnumerateArray())
                            {
                                foreach (JsonProperty envProperty in env.EnumerateObject())
                                {
                                    if (envProperty.Name == selectedC)
                                    {
                                        string jsonValue = JsonSerializer.Serialize(envProperty.Value, new JsonSerializerOptions { WriteIndented = true });
                                        settingsUpdateTextBox.Text = jsonValue;
                                        settingsLogTextBlock.Text = $"Changes for {selectedC} have been reverted." + Environment.NewLine;

                                        matchingConfigsListBox.SelectedItem = null;
                                        settingsUpdateTextBox.Visibility = Visibility.Collapsed;
                                        settingsUpdateCommands.Visibility = Visibility.Collapsed;
                                        settingsLogTextBlock.Visibility = Visibility.Visible;
                                        return;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    settingsLogTextBlock.Text = $"Error: {ex.Message}" + Environment.NewLine;
                }
            }
        }

        private void EditSelectedSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (matchingConfigsListBox.SelectedItem is string selectedConfig)
            {
                string selectedC = selectedConfig.Replace("⚠️ ", string.Empty);
                try
                {
                    string settingsFilePath = GetSettingsFilePath(settingsSolutionZipTextBox.Text);

                    if (File.Exists(settingsFilePath))
                    {
                        string jsonContent = File.ReadAllText(settingsFilePath);
                        using JsonDocument document = JsonDocument.Parse(jsonContent);
                        JsonElement root = document.RootElement;

                        if (root.TryGetProperty("Environments", out JsonElement environments))
                        {
                            foreach (JsonElement env in environments.EnumerateArray())
                            {
                                foreach (JsonProperty envProperty in env.EnumerateObject())
                                {
                                    if (envProperty.Name == selectedC)
                                    {
                                        string jsonValue = JsonSerializer.Serialize(envProperty.Value, new JsonSerializerOptions { WriteIndented = true });
                                        settingsUpdateTextBox.Text = jsonValue;
                                        settingsUpdateTextBox.Visibility = Visibility.Visible;
                                        settingsUpdateCommands.Visibility = Visibility.Visible;
                                        settingsLogTextBlock.Visibility = Visibility.Collapsed;
                                        return;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    settingsLogTextBlock.Text = $"Error: {ex.Message}" + Environment.NewLine;
                }
            }
        }

        private async void ExportJobLogButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.Desktop;
            picker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                string filePath = Path.Combine(folder.Path, "JobLog.csv");
                await ExportJobLogToCsv(filePath);
            }
        }

        private void PivotRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton radioButton && radioButton.Content != null)
            {
                switch (radioButton.Content.ToString())
                {
                    case "1. Export":
                        exportContent.Visibility = Visibility.Visible;
                        settingsContent.Visibility = Visibility.Collapsed;
                        importContent.Visibility = Visibility.Collapsed;
                        break;
                    case "2. Settings":
                        exportContent.Visibility = Visibility.Collapsed;
                        settingsContent.Visibility = Visibility.Visible;
                        importContent.Visibility = Visibility.Collapsed;
                        break;
                    case "3. Import":
                        exportContent.Visibility = Visibility.Collapsed;
                        settingsContent.Visibility = Visibility.Collapsed;
                        importContent.Visibility = Visibility.Visible;
                        break;
                }
            }
        }

        private void solutionStrategy_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton radioButton && radioButton.Content != null)
            {
                if (customVersionPanel == null || solutionStrategyHelpText == null || unchangedVersionHelpText == null)
                {
                    Debug.WriteLine("One or more UI elements are null.");
                    return;
                }
                switch (radioButton.Content.ToString())
                {
                    case "Custom":
                        customVersionPanel.Visibility = Visibility.Visible;
                        solutionStrategyHelpText.Visibility = Visibility.Collapsed;
                        unchangedVersionHelpText.Visibility = Visibility.Collapsed;
                        break;
                    case "Solution Strategy":
                        customVersionPanel.Visibility = Visibility.Collapsed;
                        solutionStrategyHelpText.Visibility = Visibility.Visible;
                        unchangedVersionHelpText.Visibility = Visibility.Collapsed;
                        break;
                    case "Unchanged":
                        customVersionPanel.Visibility = Visibility.Collapsed;
                        solutionStrategyHelpText.Visibility = Visibility.Collapsed;
                        unchangedVersionHelpText.Visibility = Visibility.Visible;
                        break;
                }
            }
        }

        private async void EnvironmentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (environmentList.SelectedItem is EnvironmentProfile selectedEnvironment)
            {
                solutionsListPanel.Visibility = Visibility.Visible;
                progressRingOverlay.Visibility = Visibility.Visible;
                solutionDetailsPanel.Visibility = Visibility.Collapsed;

                // Retrieve the list of solutions for the selected environment
                string? output = await RunPowerShellScriptAsync($"pac solution list -env {selectedEnvironment.EnvironmentUrl}");
                if (!string.IsNullOrEmpty(output))
                {
                    // Store the output in a string variable
                    string solutionListOutput = output;
                    Debug.WriteLine(solutionListOutput);

                    // Parse the solution profiles
                    solutionProfiles = ParseSolutionProfiles(solutionListOutput);
                    solutionsList.ItemsSource = solutionProfiles;
                }
            }
            else
            {
                solutionsListPanel.Visibility = Visibility.Collapsed;
            }
            progressRingOverlay.Visibility = Visibility.Collapsed;
        }

        private void SolutionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (solutionsList.SelectedItem is SolutionProfile selectedSolution)
            {
                solutionDetailsPanel.Visibility = Visibility.Visible;
            }
            else
            {
                solutionDetailsPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void JobsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (jobsListBox.SelectedItem is RunningJob selectedJob)
            {
                jobNameTextBlock.Text = selectedJob.Name;
                jobStatusTextBlock.Text = selectedJob.Status;
                jobEnvironmentTextBlock.Text = selectedJob.Environment ?? "N/A";
                jobTimestampTextBlock.Text = selectedJob.Timestamp.ToString("g");
                jobOutputTextBlock.Text = selectedJob.Output ?? "N/A";
                jobErrorTextBlock.Text = selectedJob.Error ?? "N/A";
                jobIdTextBlock.Text = selectedJob.Id;
                jobPredecessorTextBlock.Text = selectedJob.PredecessorId ?? "N/A";

                _ = jobDetailsDialog.ShowAsync();
            }
        }

        private void MatchingConfigsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (matchingConfigsListBox.SelectedItem is string selectedConfig)
            {
                
                string selectedC = selectedConfig.Replace("⚠️ ", string.Empty);
                removeSelectedSettingsButton.Visibility = Visibility.Visible;
                editSelectedSettingsButton.Visibility = Visibility.Visible;
                try
                {
                    string settingsFilePath = GetSettingsFilePath(settingsSolutionZipTextBox.Text);

                    if (File.Exists(settingsFilePath))
                    {
                        string jsonContent = File.ReadAllText(settingsFilePath);
                        using JsonDocument document = JsonDocument.Parse(jsonContent);
                        JsonElement root = document.RootElement;

                        if (root.TryGetProperty("Environments", out JsonElement environments))
                        {
                            foreach (JsonElement env in environments.EnumerateArray())
                            {
                                foreach (JsonProperty envProperty in env.EnumerateObject())
                                {
                                    if (envProperty.Name == selectedC)
                                    {
                                        string jsonValue = JsonSerializer.Serialize(envProperty.Value, new JsonSerializerOptions { WriteIndented = true });

                                        if (selectedConfig.IndexOf("⚠️ ") != -1)
                                        {
                                            settingsUpdateTextBox.Text = jsonValue;
                                            settingsUpdateTextBox.Visibility = Visibility.Visible;
                                            settingsUpdateCommands.Visibility = Visibility.Visible;
                                            settingsLogTextBlock.Visibility = Visibility.Collapsed;
                                            return;
                                        }
                                        else
                                        {
                                            settingsLogTextBlock.Text = jsonValue;
                                            settingsLogTextBlock.Visibility = Visibility.Visible;
                                            settingsUpdateTextBox.Visibility = Visibility.Collapsed;
                                            settingsUpdateCommands.Visibility = Visibility.Collapsed;
                                            return;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    settingsLogTextBlock.Text = $"Error: {ex.Message}" + Environment.NewLine;
                }
            }
            else
            {
                removeSelectedSettingsButton.Visibility = Visibility.Collapsed;
                editSelectedSettingsButton.Visibility = Visibility.Collapsed;
            }
        }

        #endregion

        #region Helper Methods

        private void InitializeMsalClient()
        {
            _msalClient = ConfidentialClientApplicationBuilder.Create(_clientId)
                .WithClientSecret(_clientSecret)
                .WithAuthority(new Uri($"https://login.microsoftonline.us/{_tenantId}"))
                .Build();

            //_msalClient = PublicClientApplicationBuilder.Create(_clientId)
            //    .WithAuthority(new Uri($"https://login.microsoftonline.com/{_tenantId}"))
            //    .WithDefaultRedirectUri()
            //    .Build();
        }

        private ServiceClient GetServiceClient(string organizationUrl)
        {
            try
            {
                var serviceClient = new ServiceClient(
                    new Uri(organizationUrl),
                    _clientId,
                    _clientSecret,
                    true
                );
                return serviceClient;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error initializing ServiceClient: {ex.Message}");
                return null;
            }
        }

        //private async Task<ServiceClient> GetServiceClientAsync(string organizationUrl)
        //{
        //    try
        //    {
        //        var connectionString = $"AuthType=OAuth;ServiceUri={organizationUrl};AccessToken={userToken};";
        //        var serviceClient = new ServiceClient(connectionString);
        //        return serviceClient;
        //    }
        //    catch
        //    {
        //        try
        //        {
        //            var result = await SignInUserAsync();
        //            userToken = result.AccessToken;
        //            var connectionString = $"AuthType=OAuth;ServiceUri={organizationUrl};AccessToken={userToken};";
        //            var serviceClient = new ServiceClient(connectionString);
        //            return serviceClient;
        //        }
        //        catch (Exception ex)
        //        {
        //            Debug.WriteLine($"Error acquiring token: {ex.Message}");

        //            //This is ok for now, but eventually we need to store this failure and then hide the option to increment version on server
        //            return null;
        //        }
        //    }
        //}

        private async Task<AuthenticationResult> SignInUserAsync()
        {
            AuthenticationResult result = null;

            try
            {
                result = await _msalClient.AcquireTokenForClient(_scopes)
                        .ExecuteAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error acquiring token: {ex.Message}");
            }
            //try
            //{
            //    var accounts = await _msalClient.GetAccountsAsync();
            //    result = await _msalClient.AcquireTokenSilent(_scopes, accounts.FirstOrDefault())
            //        .ExecuteAsync();
            //}
            //catch (MsalUiRequiredException)
            //{
            //    try
            //    {
            //result = await _msalClient.AcquireTokenInteractive(_scopes)
            //    .WithAccount(accounts.FirstOrDefault())
            //    .WithPrompt(Prompt.SelectAccount)
            //    .ExecuteAsync();
            //    }
            //    catch (Exception ex)
            //    {
            //        Debug.WriteLine($"Error acquiring token: {ex.Message}");
            //    }
            //}
            //catch (Exception ex)
            //{
            //    Debug.WriteLine($"Error acquiring token silently: {ex.Message}");
            //}

            return result;
        }

        private void CopyDirectory(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string targetFilePath = Path.Combine(targetDir, Path.GetFileName(file));
                File.Copy(file, targetFilePath, true);
            }

            foreach (var directory in Directory.GetDirectories(sourceDir))
            {
                string targetDirectoryPath = Path.Combine(targetDir, Path.GetFileName(directory));
                CopyDirectory(directory, targetDirectoryPath);
            }
        }

        private string GetSettingsFilePath(string solutionZipPath)
        {
            string tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDirectory);

            try
            {
                using (ZipArchive archive = ZipFile.OpenRead(solutionZipPath))
                {
                    var solutionEntry = archive.GetEntry("solution.xml");
                    if (solutionEntry != null)
                    {
                        string solutionXmlPath = Path.Combine(tempDirectory, "solution.xml");
                        solutionEntry.ExtractToFile(solutionXmlPath);

                        var doc = XDocument.Load(solutionXmlPath);
                        var solutionName = doc.Descendants("UniqueName").FirstOrDefault()?.Value;

                        if (!string.IsNullOrEmpty(solutionName))
                        {
                            string binDirectory = AppContext.BaseDirectory;
                            return Path.Combine(binDirectory, $"{solutionName}.settings.json");
                        }
                    }
                }
            }
            finally
            {
                Directory.Delete(tempDirectory, true);
            }

            throw new InvalidOperationException("Solution name not found in solution.xml or solution.xml not found in the provided zip file.");
        }

        private void CheckStoredSettings(string path)
        {
            try
            {
                string settingsFilePath = GetSettingsFilePath(path);

                if (File.Exists(settingsFilePath))
                {
                    string jsonContent = File.ReadAllText(settingsFilePath);
                    using JsonDocument document = JsonDocument.Parse(jsonContent);
                    JsonElement root = document.RootElement;

                    matchingSettings.Clear();

                    if (root.TryGetProperty("Environments", out JsonElement environments))
                    {
                        foreach (JsonElement env in environments.EnumerateArray())
                        {
                            foreach (JsonProperty envProperty in env.EnumerateObject())
                            {
                                matchingSettings.Add(envProperty.Name);
                            }
                        }
                    }

                    string message = $"Settings file {settingsFilePath} found.";
                    settingsLogTextBlock.Text += message + Environment.NewLine;
                    settingsResults.Visibility = Visibility.Visible;
                    matchingConfigs.Visibility = Visibility.Visible;
                    matchingConfigsListBox.Visibility = Visibility.Visible;
                    storedSettingsSetup.Visibility = Visibility.Visible;
                    singleSettingsFilePicker.Visibility = Visibility.Collapsed;
                    matchingConfigsNoneBox.Visibility = Visibility.Collapsed;
                }
                else
                {
                    string message = $"Settings file {settingsFilePath} not found.";
                    settingsLogTextBlock.Text += message + Environment.NewLine;
                    matchingConfigs.Visibility = Visibility.Visible;
                    matchingConfigsListBox.Visibility = Visibility.Collapsed;
                    matchingConfigsNoneBox.Visibility = Visibility.Visible;
                    storedSettingsSetup.Visibility = Visibility.Collapsed;
                    singleSettingsFilePicker.Visibility = Visibility.Visible;
                    settingsResults.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                string message = $"Error: {ex.Message}";
                settingsLogTextBlock.Text += message + Environment.NewLine;
                matchingConfigsListBox.Visibility = Visibility.Collapsed;
                matchingConfigsNoneBox.Visibility = Visibility.Visible;
                storedSettingsSetup.Visibility = Visibility.Collapsed;
                singleSettingsFilePicker.Visibility = Visibility.Visible;
                settingsResults.Visibility = Visibility.Visible;
            }
        }

        private void ValidateEnvironmentSettings(string environmentName, JsonElement newSettings, JsonElement storedSettings)
        {
            bool hasIssues = false;
            var issues = new List<string>();

            if (newSettings.TryGetProperty("EnvironmentVariables", out JsonElement newEnvVars) &&
                storedSettings.TryGetProperty("EnvironmentVariables", out JsonElement storedEnvVars))
            {
                var storedEnvVarsList = storedEnvVars.EnumerateArray().ToList();
                foreach (JsonElement newEnvVar in newEnvVars.EnumerateArray())
                {
                    string schemaName = newEnvVar.GetProperty("SchemaName").GetString();
                    string? newValue = GetJsonElementValueAsString(newEnvVar, "Value");

                    // Check if the schemaName exists in storedSettings
                    var storedEnvVar = storedEnvVarsList.FirstOrDefault(ev => ev.GetProperty("SchemaName").GetString() == schemaName);

                    if (storedEnvVar.ValueKind != JsonValueKind.Undefined)
                    {
                        if(!string.IsNullOrEmpty(GetJsonElementValueAsString(storedEnvVar, "Value")))
                        {
                            continue;
                        }
                        else
                        {
                            hasIssues = true;
                            issues.Add($"Existing Environment Variable '{schemaName}' has an empty or null value.");
                            continue;
                        }
                    }

                    if (string.IsNullOrEmpty(newValue))
                    {
                        hasIssues = true;
                        issues.Add($"New Environment Variable '{schemaName}' has an empty or null value. Select the stored config at the left to update the value.");
                    }
                    

                    storedEnvVarsList.Add(newEnvVar);
                }

                // Find the correct environment object within the Environments array and update it
                string settingsFilePath = GetSettingsFilePath(settingsSolutionZipTextBox.Text);
                string jsonContent = File.ReadAllText(settingsFilePath);
                using JsonDocument document = JsonDocument.Parse(jsonContent);
                JsonElement root = document.RootElement;

                if (root.TryGetProperty("Environments", out JsonElement environments))
                {
                    var environmentsList = environments.EnumerateArray().ToList();
                    for (int i = 0; i < environmentsList.Count; i++)
                    {
                        var env = environmentsList[i];
                        if (env.TryGetProperty(environmentName, out JsonElement envObject))
                        {
                            var updatedEnvObject = new Dictionary<string, JsonElement>
                            {
                                { "EnvironmentVariables", JsonDocument.Parse(JsonSerializer.Serialize(storedEnvVarsList)).RootElement }
                            };

                            var updatedEnv = new Dictionary<string, JsonElement>
                            {
                                { environmentName, JsonDocument.Parse(JsonSerializer.Serialize(updatedEnvObject)).RootElement }
                            };

                            environmentsList[i] = JsonDocument.Parse(JsonSerializer.Serialize(updatedEnv)).RootElement;
                            break;
                        }
                    }

                    var updatedJson = new
                    {
                        Environments = environmentsList
                    };

                    string updatedJsonContent = JsonSerializer.Serialize(updatedJson, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(settingsFilePath, updatedJsonContent);
                }
            }

            // Handle ConnectionReferences
            if (newSettings.TryGetProperty("ConnectionReferences", out JsonElement newConnRefs) &&
                storedSettings.TryGetProperty("ConnectionReferences", out JsonElement storedConnRefs))
            {
                var storedConnRefsList = storedConnRefs.EnumerateArray().ToList();
                foreach (JsonElement newConnRef in newConnRefs.EnumerateArray())
                {
                    string logicalName = newConnRef.GetProperty("LogicalName").GetString();
                    string? newConnectionId = GetJsonElementValueAsString(newConnRef, "ConnectionId");
                    string? newConnectorId = GetJsonElementValueAsString(newConnRef, "ConnectorId");

                    // Check if the logicalName exists in storedSettings
                    var storedConnRef = storedConnRefsList.FirstOrDefault(cr => cr.GetProperty("LogicalName").GetString() == logicalName);

                    if (storedConnRef.ValueKind != JsonValueKind.Undefined)
                    {
                        if (!string.IsNullOrEmpty(GetJsonElementValueAsString(storedConnRef, "ConnectionId")) &&
                            !string.IsNullOrEmpty(GetJsonElementValueAsString(storedConnRef, "ConnectorId")))
                        {
                            continue;
                        }
                        else
                        {
                            hasIssues = true;
                            issues.Add($"Existing Connection Reference '{logicalName}' has an empty or null ConnectionId or ConnectorId.");
                            continue;
                        }
                    }

                    if (string.IsNullOrEmpty(newConnectionId) || string.IsNullOrEmpty(newConnectorId))
                    {
                        hasIssues = true;
                        issues.Add($"New Connection Reference '{logicalName}' has an empty or null ConnectionId or ConnectorId. Select the stored config at the left to update the value.");
                    }

                    storedConnRefsList.Add(newConnRef);
                }

                // Find the correct environment object within the Environments array and update it
                string settingsFilePath = GetSettingsFilePath(settingsSolutionZipTextBox.Text);
                string jsonContent = File.ReadAllText(settingsFilePath);
                using JsonDocument document = JsonDocument.Parse(jsonContent);
                JsonElement root = document.RootElement;

                if (root.TryGetProperty("Environments", out JsonElement environments))
                {
                    var environmentsList = environments.EnumerateArray().ToList();
                    for (int i = 0; i < environmentsList.Count; i++)
                    {
                        var env = environmentsList[i];
                        if (env.TryGetProperty(environmentName, out JsonElement envObject))
                        {
                            var updatedEnvObject = new Dictionary<string, JsonElement>
                    {
                        { "ConnectionReferences", JsonDocument.Parse(JsonSerializer.Serialize(storedConnRefsList)).RootElement }
                    };

                            var updatedEnv = new Dictionary<string, JsonElement>
                    {
                        { environmentName, JsonDocument.Parse(JsonSerializer.Serialize(updatedEnvObject)).RootElement }
                    };

                            environmentsList[i] = JsonDocument.Parse(JsonSerializer.Serialize(updatedEnv)).RootElement;
                            break;
                        }
                    }

                    var updatedJson = new
                    {
                        Environments = environmentsList
                    };

                    string updatedJsonContent = JsonSerializer.Serialize(updatedJson, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(settingsFilePath, updatedJsonContent);
                }
            }

            if (hasIssues)
            {
                settingsLogTextBlock.Text += $"Issues found in environment '{environmentName}':" + Environment.NewLine;
                foreach (var issue in issues)
                {
                    settingsLogTextBlock.Text += "⚠️ " + issue + Environment.NewLine;
                }

                // Remove and re-add the item in the ListBox
                if (matchingSettings.Contains(environmentName))
                {
                    matchingSettings.Remove(environmentName);
                    matchingSettings.Add($"⚠️ {environmentName}");
                }
            }
            else
            {
                // Check if the environmentName has been flagged with the warning icon
                string flaggedEnvironmentName = $"⚠️ {environmentName}";
                if (matchingSettings.Contains(flaggedEnvironmentName))
                {
                    matchingSettings.Remove(flaggedEnvironmentName);
                    matchingSettings.Add(environmentName);
                }
            }
        }

        private string? GetJsonElementValueAsString(JsonElement element, string propertyName)
        {
            try
            {
                if (element.ValueKind != JsonValueKind.Undefined && element.TryGetProperty(propertyName, out JsonElement propertyElement))
                {
                    if (propertyElement.ValueKind == JsonValueKind.Undefined)
                    {
                        Debug.WriteLine($"Property '{propertyName}' is undefined.");
                        return null;
                    }

                    return propertyElement.ValueKind switch
                    {
                        JsonValueKind.String => propertyElement.GetString(),
                        JsonValueKind.Number => propertyElement.GetRawText(),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        _ => null
                    };
                }
                else
                {
                    Debug.WriteLine($"Property '{propertyName}' not found or element is undefined.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error accessing property '{propertyName}': {ex.Message}");
            }
            return null;
        }

        private bool IsValidJson(string jsonString)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(jsonString);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private void ShowErrorDialog(string message)
        {
            errorDialogText.Text = message;
            errorDialog.ShowAsync();
        }

        private void SaveEnvironmentToCsv(EnvironmentProfile environmentProfile)
        {
            string binDirectory = AppContext.BaseDirectory;
            string csvFilePath = Path.Combine(binDirectory, "environments.csv");

            bool fileExists = File.Exists(csvFilePath);

            using (var writer = new StreamWriter(csvFilePath, append: true))
            {
                if (!fileExists)
                {
                    // Write the header if the file does not exist
                    writer.WriteLine("DisplayName,EnvironmentId,EnvironmentUrl,UniqueName,Active");
                }

                // Write the environment information
                writer.WriteLine($"{environmentProfile.DisplayName},{environmentProfile.EnvironmentId},{environmentProfile.EnvironmentUrl},{environmentProfile.UniqueName},{environmentProfile.Active}");
            }
        }

        private void ReadEnvironmentsFromCsv()
        {
            string binDirectory = AppContext.BaseDirectory;
            string csvFilePath = Path.Combine(binDirectory, "environments.csv");
            if (!File.Exists(csvFilePath))
            {
                return;
            }
            using (var reader = new StreamReader(csvFilePath))
            {
                string headerLine = reader.ReadLine(); // Skip the header line
                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    var values = line.Split(',');
                    if (values.Length >= 5)
                    {
                        var environmentProfile = new EnvironmentProfile
                        {
                            DisplayName = values[0],
                            EnvironmentId = values[1],
                            EnvironmentUrl = values[2],
                            UniqueName = values[3],
                            Active = bool.Parse(values[4])
                        };
                        environmentProfiles.Add(environmentProfile);
                        environmentList.ItemsSource = null;
                        environmentList.ItemsSource = environmentProfiles;
                        importEnvironmentList.ItemsSource = null;
                        importEnvironmentList.ItemsSource = environmentProfiles;
                    }
                }
            }
        }

        private bool EnvironmentExistsInCsv(string environmentUrl)
        {
            string binDirectory = AppContext.BaseDirectory;
            string csvFilePath = Path.Combine(binDirectory, "environments.csv");

            if (!File.Exists(csvFilePath))
            {
                return false;
            }

            using (var reader = new StreamReader(csvFilePath))
            {
                string headerLine = reader.ReadLine(); // Skip the header line
                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    var values = line.Split(',');

                    if (values.Length >= 3 && values[2].Equals(environmentUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool CsvFileHasRows()
        {
            string binDirectory = AppContext.BaseDirectory;
            string csvFilePath = Path.Combine(binDirectory, "environments.csv");

            if (!File.Exists(csvFilePath))
            {
                return false;
            }

            using (var reader = new StreamReader(csvFilePath))
            {
                string headerLine = reader.ReadLine(); // Skip the header line
                return !reader.EndOfStream; // Check if there are any rows after the header
            }
        }

        private async Task ExportJobLogToCsv(string filePath)
        {
            try
            {
                using (var writer = new StreamWriter(filePath))
                {
                    // Write the header
                    await writer.WriteLineAsync("Id,Name,Status,Environment,Output,Error,Timestamp,PredecessorId");

                    // Write the job log entries
                    foreach (var job in jobs)
                    {
                        string line = $"{job.Id},{job.Name},{job.Status},{job.Environment},{job.Output},{job.Error},{job.Timestamp},{job.PredecessorId}";
                        await writer.WriteLineAsync(line);
                    }
                }

                ShowErrorDialog("Job log exported successfully.");
            }
            catch (Exception ex)
            {
                ShowErrorDialog($"Error exporting job log: {ex.Message}");
            }
        }
        #endregion
    }

    #region Models
    public class GuidConfig
    {
        public required string GUID { get; set; }
        public required string NewValue { get; set; }
        public required string TargetEnvironment { get; set; }
    }

    public class AuthProfile
    {
        public int Index { get; set; }
        public bool Active { get; set; }
        public required string Kind { get; set; }
        public required string Name { get; set; }
        public required string User { get; set; }
        public string? Cloud { get; set; }
        public string? Type { get; set; }
        public required string Environment { get; set; }
        public required string EnvironmentUrl { get; set; }
        public string DisplayName => $"{Name} ({Environment})";
    }

    public class EnvironmentProfile
    {
        public required string DisplayName { get; set; }
        public required string EnvironmentId { get; set; }
        public required string EnvironmentUrl { get; set; }
        public required string UniqueName { get; set; }
        public bool Active { get; set; }
    }

    public class SolutionProfile
    {
        public required string UniqueName { get; set; }
        public required string FriendlyName { get; set; }
        public required string Version { get; set; }
    }

    public class RunningJob
    {
        public string Id { get; } = Guid.NewGuid().ToString(); // Unique ID for the job
        public required string Name { get; set; }
        public required string Status { get; set; }
        public string? Environment { get; set; }
        public string? Output { get; set; }
        public string? Error { get; set; }
        public required DateTime Timestamp { get; set; }
        public string DisplayName => $"{(Status == "In Progress" ? "🏃‍♂️" : Status == "Failed" ? "🤬" : Status == "Waiting" ? "⏳" : "🥳")} {Name}";
        public string? PredecessorId { get; set; } // The ID of the predecessor job
        public Func<RunningJob, Task>? JobLogic { get; set; } // The logic to be executed for the job
    }

    public class EVSettings
    {
        public required string SchemaName { get; set; }
        public required string Value { get; set; }
        public required string Type { get; set; }
        public required string TargetEnvironment { get; set; }
    }

    public class CRSettings
    {
        public required string LogicalName { get; set; }
        public required string ConnectionId { get; set; }
        public required string ConnectorId { get; set; }
        public required string TargetEnvironment { get; set; }
    }

    public class GlobalDiscoveryResponse
    {
        public List<Instance> value { get; set; }
    }

    public class Instance
    {
        public string FriendlyName { get; set; }
        public string EnvironmentId { get; set; }
        public string ApiUrl { get; set; }
        public string UniqueName { get; set; }
        public string State { get; set; }
    }
    #endregion
}
