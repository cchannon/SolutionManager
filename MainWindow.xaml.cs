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

namespace SolutionManager
{
    public sealed partial class MainWindow : Window
    {
        List<AuthProfile> authProfiles = new();
        List<EnvironmentProfile> environmentProfiles = new();
        List<SolutionProfile> solutionProfiles = new();
        ObservableCollection<RunningJob> jobs = new();
        private bool isInitialized = false;
        List<GuidConfig> dataTable = new();
        List<EVSettings> evTable = new();
        List<CRSettings> crTable = new();
        private string zipFileNameWithoutExtension = "";
        private Queue<RunningJob> jobQueue = new();
        private bool isJobRunning = false;

        public MainWindow()
        {
            this.InitializeComponent();
            jobsListBox.ItemsSource = jobs;
            isInitialized = true;
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
                        case "updateGuidsTargetDirBrowse":
                            updateGuidsTargetDirTextBox.Text = folder.Path;
                            break;
                        case "settingsTargetDirBrowse":
                            settingsTargetDirTextBox.Text = folder.Path;
                            UpdateGenerateSettingsButtonVisibility();
                            break;
                    }

                    // Check if both the ZIP file and CSV file have been processed and a target directory has been selected
                    if (button.Name == "updateGuidsTargetDirBrowse" && !string.IsNullOrEmpty(updateZipFilePathTextBox.Text) && !string.IsNullOrEmpty(updateCsvFilePathTextBox.Text) && !string.IsNullOrEmpty(updateGuidsTargetDirTextBox.Text))
                    {
                        swapGuidsButton.Visibility = Visibility.Visible;
                    }
                    else if (button.Name == "updateGuidsTargetDirBrowse")
                    {
                        exportButton.Visibility = Visibility.Collapsed;
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
                { "settingsZipFilePathBrowse", ".zip" },
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
                        case "updateGuidsZipBrowse":
                            updateZipFilePathTextBox.Text = file.Path;
                            zipFileNameWithoutExtension = Path.GetFileNameWithoutExtension(file.Path);
                            string tempDirectory = ExtractZipFile(file.Path);
                            Debug.WriteLine($"Extracted to: {tempDirectory}");
                            break;
                        case "updateGuidsCsvBrowse":
                            updateCsvFilePathTextBox.Text = file.Path;
                            ReadCsvFile(file.Path, sender);
                            csvPreviewDataGrid.ItemsSource = dataTable;
                            break;
                        case "settingsZipFilePathBrowse":
                            settingsZipFilePathTextBox.Text = file.Path;
                            UpdateGenerateSettingsButtonVisibility();
                            break;
                        case "evCsvFilePathBrowse":
                            evCsvFilePathTextBox.Text = file.Path;
                            ReadCsvFile(file.Path, sender);
                            evPreviewDataGrid.ItemsSource = evTable;
                            UpdateGenerateSettingsButtonVisibility();
                            break;
                        case "crCsvFilePathBrowse":
                            crCsvFilePathTextBox.Text = file.Path;
                            ReadCsvFile(file.Path, sender);
                            crPreviewDataGrid.ItemsSource = crTable;
                            UpdateGenerateSettingsButtonVisibility();
                            break;
                    }

                    // Check if both the ZIP file and CSV file have been processed and a target directory has been selected
                    if (!string.IsNullOrEmpty(updateZipFilePathTextBox.Text) && !string.IsNullOrEmpty(updateCsvFilePathTextBox.Text) && !string.IsNullOrEmpty(updateGuidsTargetDirTextBox.Text))
                    {
                        swapGuidsButton.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        swapGuidsButton.Visibility = Visibility.Collapsed;
                    }
                }
            }
        }

        private async void ExportButton_Click(object sender, RoutedEventArgs e)
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
                // Handle the case where no environment or solution is selected
                Debug.WriteLine("No environment or solution selected.");
            }
        }

        private async void PublishButton_Click(object sender, RoutedEventArgs e)
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

        private async Task<string?> RunPowerShellScriptAsync(string scriptText)
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
                    CreateNoWindow = true
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

        private void TryStartNextJob()
        {
            if (isJobRunning || jobQueue.Count == 0)
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
            }

            nextJob = jobQueue.Dequeue();
            nextJob.Status = "In Progress";
            isJobRunning = true;

            _ = Task.Run(async () =>
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
                    isJobRunning = false;
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

        private void UpdateGenerateSettingsButtonVisibility()
        {
            bool isSingleFileMode = generateModeToggle.IsOn;
            bool isSettingsZipFilePathSet = !string.IsNullOrEmpty(settingsZipFilePathTextBox.Text);
            bool isSettingsTargetDirSet = !string.IsNullOrEmpty(settingsTargetDirTextBox.Text);
            bool isEvCsvFilePathSet = !string.IsNullOrEmpty(evCsvFilePathTextBox.Text);
            bool isCrCsvFilePathSet = !string.IsNullOrEmpty(crCsvFilePathTextBox.Text);

            if ((isSingleFileMode && isSettingsZipFilePathSet && (isEvCsvFilePathSet || isCrCsvFilePathSet)) ||
                (!isSingleFileMode && isSettingsTargetDirSet && (isEvCsvFilePathSet || isCrCsvFilePathSet)))
            {
                generateSettings.Visibility = Visibility.Visible;
            }
            else
            {
                generateSettings.Visibility = Visibility.Collapsed;
            }
        }

        private void GenerateSettings_Click(object sender, RoutedEventArgs e)
        {
            if (generateModeToggle.IsOn) // Single Solution mode
            {
                var zipFilePath = settingsZipFilePathTextBox.Text;
                var targetDir = Path.GetDirectoryName(settingsZipFilePathTextBox.Text);
                var settingsFilePath = Path.Combine(targetDir, "deploymentsettings.json");

                // Construct the pac solution create-settings command
                var command = $"pac solution create-settings -z \'{zipFilePath}\' -s \'{settingsFilePath}\'";

                // Create a new job and add it to the jobs list
                var job = new RunningJob
                {
                    Name = $"Create Settings: {Path.GetFileName(zipFilePath)}",
                    Status = "Waiting",
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
            else // Directory mode
            {
                var targetDir = settingsTargetDirTextBox.Text;

                // Recursively search through the directory and all subdirectories to find every zip file
                var zipFiles = Directory.GetFiles(targetDir, "*.zip", SearchOption.AllDirectories);

                foreach (var zipFilePath in zipFiles)
                {
                    var settingsFilePath = Path.Combine(Path.GetDirectoryName(zipFilePath), "deploymentsettings.json");

                    // Construct the pac solution create-settings command
                    var command = $"pac solution create-settings -z \'{zipFilePath}\' -s \'{settingsFilePath}\'";

                    // Create a new job and add it to the jobs list
                    var job = new RunningJob
                    {
                        Name = $"Create Settings: {Path.GetFileName(zipFilePath)}",
                        Status = "Waiting",
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
            }
        }

        private void ReplaceStrings_Click(object sender, RoutedEventArgs e)
        {
            string extractedFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EnvironmentManagement", "Temp");
            string targetDir = updateGuidsTargetDirTextBox.Text;

            // Group the replacements by TargetEnvironment
            var groupedReplacements = dataTable.GroupBy(cfg => cfg.TargetEnvironment);

            foreach (var group in groupedReplacements)
            {
                string targetEnvironment = group.Key;
                // Create a new job and add it to the jobs list
                var job = new RunningJob
                {
                    Name = $"Replace Strings: {targetEnvironment}",
                    Status = "In Progress",
                    Timestamp = DateTime.Now,
                };
                jobs.Add(job);
                jobsPanel.Visibility = Visibility.Visible;

                // Run the replacement and writing operations on a separate thread
                _ = Task.Run(async () =>
                {
                    try
                    {
                        string groupTargetDir = Path.Combine(targetDir, group.Key);
                        string customizationsDir = Path.Combine(groupTargetDir, zipFileNameWithoutExtension);

                        // Check if the customizationsDir exists and delete it if it does
                        if (Directory.Exists(customizationsDir))
                        {
                            Directory.Delete(customizationsDir, true);
                        }

                        string newZipFilePath = Path.Combine(groupTargetDir, $"{zipFileNameWithoutExtension}.zip");
                        if (File.Exists(newZipFilePath))
                        {
                            File.Delete(newZipFilePath);
                        }

                        // Create a clone of the extracted folder
                        CopyDirectory(extractedFolderPath, groupTargetDir);

                        // Path to the customizations.xml file in the target folder
                        string customizationsFilePath = Path.Combine(customizationsDir, "customizations.xml");

                        if (File.Exists(customizationsFilePath))
                        {
                            int replacementCount = 0;
                            string fileContent = await File.ReadAllTextAsync(customizationsFilePath);

                            // Perform the replacements
                            foreach (var replacement in group)
                            {
                                if (fileContent.Contains(replacement.GUID))
                                {
                                    fileContent = fileContent.Replace(replacement.GUID, replacement.NewValue);
                                    replacementCount++;
                                }
                            }

                            // Write the modified content back to the file
                            await File.WriteAllTextAsync(customizationsFilePath, fileContent);

                            if (replacementCount > 0)
                            {
                                // Zip up the contents of the directory into a new zip file
                                ZipFile.CreateFromDirectory(customizationsDir, newZipFilePath);

                                // Delete the clone folder
                                Directory.Delete(customizationsDir, true);

                                job.Status = "Successful";
                                job.Output = $"String replacements completed successfully. {replacementCount} replacements made.";
                            }
                            else
                            {
                                // Delete the folder if no replacements were made
                                Directory.Delete(customizationsDir, true);

                                job.Status = "Successful";
                                job.Output = "No replacements were made. Directory deleted.";
                            }
                        }
                        else
                        {
                            job.Status = "Failed";
                            job.Output = $"Error: Customizations file not located.";
                        }
                    }
                    catch (Exception ex)
                    {
                        job.Status = "Failed";
                        job.Output = $"Error: {ex.Message}";
                    }

                    // Update the job status in the UI
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        jobsListBox.ItemsSource = null;
                        jobsListBox.ItemsSource = jobs;
                    });
                });
            }

            Debug.WriteLine("String replacements initiated.");
        }

        private void GenerateModeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (generateModeToggle.IsOn)
            {
                // Single File mode
                singleFilePanel.Visibility = Visibility.Visible;
                recursiveDirectoryPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Recursive Directory mode
                singleFilePanel.Visibility = Visibility.Collapsed;
                recursiveDirectoryPanel.Visibility = Visibility.Visible;
            }

            UpdateGenerateSettingsButtonVisibility();
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

        private void UpdateRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            if (isInitialized && sender is RadioButton radioButton && radioButton.Content != null)
            {
                switch (radioButton.Content.ToString())
                {
                    case "Replace GUIDs":
                        guidSwapPanel.Visibility = Visibility.Visible;
                        settingsFilePanel.Visibility = Visibility.Collapsed;
                        break;
                    case "Settings.JSON":
                        guidSwapPanel.Visibility = Visibility.Collapsed;
                        settingsFilePanel.Visibility = Visibility.Visible;
                        break;
                }
            }
        }

        private void PivotRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton radioButton && radioButton.Content != null)
            {
                switch (radioButton.Content.ToString())
                {
                    case "Export":
                        exportContent.Visibility = Visibility.Visible;
                        updateContent.Visibility = Visibility.Collapsed;
                        importContent.Visibility = Visibility.Collapsed;
                        break;
                    case "Update":
                        exportContent.Visibility = Visibility.Collapsed;
                        updateContent.Visibility = Visibility.Visible;
                        importContent.Visibility = Visibility.Collapsed;
                        // Show Update panel, hide others
                        break;
                    case "Import":
                        exportContent.Visibility = Visibility.Collapsed;
                        updateContent.Visibility = Visibility.Collapsed;
                        importContent.Visibility = Visibility.Visible;
                        // Show Import panel, hide others
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

        private async void ImportButton_Click(object sender, RoutedEventArgs e)
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
        #endregion

        #region Helper Methods
        private void ReadCsvFile(string filePath, object sender)
        {
            using (var reader = new StreamReader(filePath))
            {
                var headers = reader.ReadLine().Split(',');
                while (!reader.EndOfStream)
                {
                    var rows = reader.ReadLine().Split(',');

                    if (sender is Button button)
                    {
                        if (button.Name == "evCsvFilePathBrowse")
                        {
                            var evSetting = new EVSettings
                            {
                                SchemaName = rows[0],
                                Value = rows[1],
                                Type = rows[2],
                                TargetEnvironment = rows[3]
                            };
                            evTable.Add(evSetting);
                        }
                        else if (button.Name == "crCsvFilePathBrowse")
                        {
                            var crSetting = new CRSettings
                            {
                                LogicalName = rows[0],
                                ConnectionId = rows[1],
                                ConnectorId = rows[2],
                                TargetEnvironment = rows[3]
                            };
                            crTable.Add(crSetting);
                        }
                        else if (button.Name == "updateGuidsCsvBrowse")
                        {
                            var cfg = new GuidConfig
                            {
                                GUID = rows[0],
                                NewValue = rows[1],
                                TargetEnvironment = rows[2]
                            };
                            dataTable.Add(cfg);
                        }
                    }
                }
            }
        }

        private string ExtractZipFile(string zipFilePath)
        {
            string tempDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EnvironmentManagement", "Temp", zipFileNameWithoutExtension);

            // Check if the directory exists and delete it if it does
            if (Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EnvironmentManagement", "Temp")))
            {
                Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EnvironmentManagement", "Temp"), true);
            }

            Directory.CreateDirectory(tempDirectory);

            ZipFile.ExtractToDirectory(zipFilePath, tempDirectory);

            return tempDirectory;
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
