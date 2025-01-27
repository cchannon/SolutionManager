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
using Microsoft.UI.Xaml.Input;

namespace SolutionManager
{
    public sealed partial class MainWindow : Window
    {
        List<AuthProfile> authProfiles = new();
        List<EnvironmentProfile> environmentProfiles = new();
        List<SolutionProfile> solutionProfiles = new();
        ObservableCollection<RunningJob> jobs = new();
        ObservableCollection<string> matchingSettings = new();
        private Queue<RunningJob> jobQueue = new();
        object settingsObject;

        public MainWindow()
        {
            this.InitializeComponent();
            jobsListBox.ItemsSource = jobs;
            matchingConfigsListBox.ItemsSource = matchingSettings;

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

        public void Grid_Loaded(object sender, RoutedEventArgs e)
        {
            _ = InitializeAuthProfilesAsync();
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
            if (!string.IsNullOrEmpty(output))
            {
                environmentProfiles = ParseEnvironmentProfiles(output);

                environmentList.ItemsSource = environmentProfiles;
                importEnvironmentList.ItemsSource = environmentProfiles;
            }
            progressRingOverlay.Visibility = Visibility.Collapsed;
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
            var regex = new Regex(@"\[(\d+)\]\s+(\*?)\s+(\S+)\s+(\S+)\s+(\S+)\s+(\S+)\s+(\S+)\s+(.+?)\s+(https?://\S+)");

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
                        Environment = match.Groups[8].Value,
                        EnvironmentUrl = match.Groups[9].Value
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
        private async void EnvironmentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (environmentList.SelectedItem is EnvironmentProfile selectedEnvironment)
            {
                solutionsListPanel.Visibility = Visibility.Visible;
                progressRingOverlay.Visibility = Visibility.Visible;
                solutionDetailsPanel.Visibility = Visibility.Collapsed;

                // Retrieve the list of solutions for the selected environment
                string? output = await RunPowerShellScriptAsync($"pac solution list -env {selectedEnvironment.EnvironmentId}");
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
                { "updateGuidsZipBrowse", ".zip" },
                { "updateGuidsCsvBrowse", ".csv" },
                { "settingsSolutionPathBrowse", ".zip" },
                { "crCsvFilePathBrowse", ".csv" },
                { "evCsvFilePathBrowse", ".csv" }

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
                            break;
                        case "importJsonFileBrowse":
                            importJsonPathTextBox.Text = file.Path;
                            break;
                        case "settingsSolutionPathBrowse":
                            settingsSolutionZipTextBox.Text = file.Path;
                            CheckStoredSettings(file.Path);
                            break;
                    }
                }
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
                    Title = "Enter Target Environment",
                    Content = new TextBox { PlaceholderText = "Target Environment Name" },
                    PrimaryButtonText = "OK",
                    CloseButtonText = "Cancel",
                    XamlRoot = this.Content.XamlRoot
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    var textBox = (TextBox)dialog.Content;
                    string targetEnvironment = textBox.Text;

                    if (string.IsNullOrEmpty(targetEnvironment))
                    {
                        // Handle the case where the user did not enter a target environment
                        settingsLogTextBlock.Text += "Target environment not specified." + Environment.NewLine;
                        return;
                    }

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
                        settingsLogTextBlock.Text += message + Environment.NewLine;

                        CheckStoredSettings(settingsSolutionZipTextBox.Text);
                    }
                    catch (Exception ex)
                    {
                        string message = $"Error: {ex.Message}";
                        settingsLogTextBlock.Text += message + Environment.NewLine;
                    }
                }
            }
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
                }
                else
                {
                    string message = $"Settings file {settingsFilePath} not found.";
                    settingsLogTextBlock.Text += message + Environment.NewLine;
                    matchingConfigs.Visibility = Visibility.Visible;
                    matchingConfigsListBox.Visibility = Visibility.Collapsed;
                    matchingConfigsNoneBox.Visibility = Visibility.Visible;
                    settingsResults.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                string message = $"Error: {ex.Message}";
                settingsLogTextBlock.Text += message + Environment.NewLine;
                matchingConfigsListBox.Visibility = Visibility.Collapsed;
                matchingConfigsNoneBox.Visibility = Visibility.Visible;
                settingsResults.Visibility = Visibility.Visible;
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
            try
            {
                string settingsFilePath = GetSettingsFilePath(settingsSolutionZipTextBox.Text);

                if (!File.Exists(settingsFilePath))
                {
                    settingsLogTextBlock.Text += "Settings file not found." + Environment.NewLine;
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
                    settingsLogTextBlock.Text += "Failed to generate new settings file." + Environment.NewLine;
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

        private void ValidateEnvironmentSettings(string environmentName, JsonElement newSettings, JsonElement storedSettings)
        {
            bool hasIssues = false;
            var issues = new List<string>();

            if (newSettings.TryGetProperty("EnvironmentVariables", out JsonElement newEnvVars) &&
                storedSettings.TryGetProperty("EnvironmentVariables", out JsonElement storedEnvVars))
            {
                foreach (JsonElement newEnvVar in newEnvVars.EnumerateArray())
                {
                    string schemaName = newEnvVar.GetProperty("SchemaName").GetString();
                    string? newValue = GetJsonElementValueAsString(newEnvVar, "Value");
                    string? newDefaultValue = GetJsonElementValueAsString(newEnvVar, "DefaultValue");

                    JsonElement? storedEnvVar = storedEnvVars.EnumerateArray().FirstOrDefault(ev => ev.GetProperty("SchemaName").GetString() == schemaName);

                    string? storedValue = null;
                    string? storedDefaultValue = null;

                    if (storedEnvVar.HasValue)
                    {
                        storedValue = GetJsonElementValueAsString(storedEnvVar.Value, "Value");
                        storedDefaultValue = GetJsonElementValueAsString(storedEnvVar.Value, "DefaultValue");
                    }

                    if (string.IsNullOrEmpty(newValue) && string.IsNullOrEmpty(newDefaultValue) &&
                        string.IsNullOrEmpty(storedValue) && string.IsNullOrEmpty(storedDefaultValue))
                    {
                        hasIssues = true;
                        issues.Add($"Environment Variable '{schemaName}' has both Value and DefaultValue missing.");
                    }
                }
            }

            if (hasIssues)
            {
                settingsLogTextBlock.Text += $"Issues found in environment '{environmentName}':" + Environment.NewLine;
                foreach (var issue in issues)
                {
                    settingsLogTextBlock.Text += issue + Environment.NewLine;
                }

                // Remove and re-add the item in the ListBox
                if (matchingSettings.Contains(environmentName))
                {
                    matchingSettings.Remove(environmentName);
                    matchingSettings.Add($"⚠️ {environmentName}");
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

                    settingsLogTextBlock.Text += $"Removed selected settings from {settingsFilePath}." + Environment.NewLine;
                }
            }
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (environmentList.SelectedItem is EnvironmentProfile selectedEnvironment &&
                solutionsList.SelectedItem is SolutionProfile selectedSolution)
            {
                var zipFilePath = zipFilePathTextBox.Text;
                var exportAsManaged = exportAsManagedCheckBox.IsChecked ?? false;
                var overwrite = overwriteCheckBox.IsChecked ?? false;
                var includeSettings = new List<string>();

                foreach (ListBoxItem item in includeSettingsListBox.SelectedItems)
                {
                    includeSettings.Add(item.Content.ToString());
                }

                // Construct the pac solution export command
                var command = $"pac solution export --environment {selectedEnvironment.EnvironmentId} --name {selectedSolution.UniqueName} --path '{zipFilePath}'";

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

                string vjobid = "";

                if(solutionStrategyRadioButtons.SelectedItem.ToString() != "unchanged")
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
                            switch (solutionStrategyRadioButtons.SelectedItem)
                            {
                                case RadioButton customRadioButton when customRadioButton.Content.ToString() == "Custom":
                                    if (!string.IsNullOrEmpty(releaseVer.Text))
                                    {
                                        command += $" --revisionversion {releaseVer.Text}";
                                    }
                                    if (!string.IsNullOrEmpty(buildVer.Text))
                                    {
                                        command += $" --buildversion {buildVer.Text}";
                                    }
                                    break;
                                case RadioButton solutionStrategyRadioButton when solutionStrategyRadioButton.Content.ToString() == "Solution Strategy":
                                    command += " -s solution";
                                    break;
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
                        PredecessorId = solutionStrategyRadioButtons.SelectedItem.ToString() != "unchanged" ? vjobid : job.Id,
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
                var command = $"pac solution publish --environment {selectedEnvironment.EnvironmentId} --async";

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
                removeSelectedSettingsButton.Visibility = Visibility.Visible;
            }
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            if (importEnvironmentList.SelectedItem is EnvironmentProfile selectedEnvironment)
            {
                var solutionFilePath = importZipPathTextBox.Text;
                var settingsFilePath = importJsonPathTextBox.Text;
                var activatePlugins = activatePluginsCheckBox.IsChecked ?? false;
                var stageAndUpgrade = stageAndUpgradeCheckBox.IsChecked ?? false;
                var publishAfterImport = publishAfterImportCheckBox.IsChecked ?? false;
                var forceOverwrite = forceOverwriteCheckBox.IsChecked ?? false;

                // Construct the pac solution import command
                var command = $"pac solution import --environment {selectedEnvironment.EnvironmentId} --path \"{solutionFilePath}\"";

                if (!string.IsNullOrEmpty(settingsFilePath))
                {
                    command += $" --settings-file \"{settingsFilePath}\"";
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
                    Environment = selectedEnvironment.EnvironmentId,
                    JobLogic = async (currentJob) =>
                    {
                        string? output = await RunPowerShellScriptAsync(command);
                        currentJob.Output = output ?? string.Empty;
                        currentJob.Status = !string.IsNullOrEmpty(output) && output.Contains("succeeded", StringComparison.OrdinalIgnoreCase) ? "Successful" : "Failed";
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

        private void CheckInAfterExport_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            CheckInInfoBar.IsOpen = true;
        }

        private void CheckInAfterExport_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            CheckInInfoBar.IsOpen = false;
        }
        #endregion

        #region Helper Methods
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
    #endregion
}
