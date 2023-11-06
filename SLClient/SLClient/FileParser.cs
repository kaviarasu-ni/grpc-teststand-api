using IniParser;

namespace SLClient
{
    internal class FileParser
    {
        private string iniFilePath;
        private FileIniDataParser parser;
        private FileSystemWatcher watcher;
        private Example example;
        private bool isPaused;
        private bool start;
        private bool pause;
        private bool terminate;

        public FileParser(string iniFilePath, Example example)
        {
            this.iniFilePath = iniFilePath;
            this.example = example;
            parser = new FileIniDataParser();
            isPaused = false;
            start = false;
            pause = false;
            terminate = false;

            watcher = new FileSystemWatcher
            {
                Path = Path.GetDirectoryName(iniFilePath),
                Filter = Path.GetFileName(iniFilePath),
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };

            watcher.Changed += OnFileChanged;

            Console.WriteLine($"Monitoring {iniFilePath} for changes. Press Enter to exit.");
            Console.ReadLine();
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            // Reload and parse the INI file on changes
            var parser = new FileIniDataParser();
            var iniData = parser.ReadFile(e.FullPath);

            // Display the updated INI file content
            Console.WriteLine("INI file contents have changed:");
            foreach (var section in iniData.Sections)
            {
                Console.WriteLine($"[{section.SectionName}]");
                foreach (var keyData in section.Keys)
                {
                    // Console.WriteLine($"{keyData.KeyName} = {keyData.Value}");
                    if (keyData.KeyName == "Start" && keyData.Value == "True" && !start)
                    {
                        setState(true, false, false);
                        Console.WriteLine("Starting the test sequence");
                        if (isPaused)
                        {
                            example.OnResumeButtonClick(null, null);
                            isPaused = false;
                        }
                        else
                        {
                            example.OnRunSequenceFileButtonClick(null, null);
                        }

                    }
                    else if (keyData.KeyName == "Pause" && keyData.Value == "True" && !pause)
                    {
                        setState(false, true, false);
                        isPaused = true;
                        Console.WriteLine("Pause");
                        example.OnBreakButtonClick(null, null);
                    }
                    else if (keyData.KeyName == "Terminate" && keyData.Value == "True" && !terminate)
                    {
                        setState(false, false, true);
                        isPaused = false;
                        Console.WriteLine("Terminate");
                        example.OnTerminateButtonClick(null, null);
                    }
                }
            }
        }

        private void setState(bool start, bool pause, bool terminate)
        {
            this.start = start;
            this.pause = pause;
            this.terminate = terminate;
        }
    }
}
