using NationalInstruments.TestStand.Grpc.Client.Utilities;
using System.IO.Pipes;

namespace SLClient
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Starting SL Client service");

            // A path to a config file can be specified in the command line. If an argument 
            // is specified, we will assume it is the path to the config file since no 
            // other command line arguments are supported.
            string configurationFile = GetConfigFilePathFromCommandLineIfSpecified(args);

            var clientConfiguration = new ClientConfiguration(
                useSecureConnection: true,
                string.IsNullOrEmpty(configurationFile) ? ClientConfiguration.DefaultConfigFilepath : configurationFile,
                ClientConfiguration.DefaultCertificatesFolderpath);
            ClientOptions options = clientConfiguration.Options;

            var example = new Example(options);
            //example.OnConnectButtonClick(null, null);
            //example.OnRunSequenceFileButtonClick(null, null);
            CreateNamedPipe(example);
            var parser = new FileParser(
                "C:\\ProgramData\\National Instruments\\Skyline\\TestPlan\\TestSequences\\INI\\TestFile.ini",
                example);

            Console.ReadLine();
        }

        private static string GetConfigFilePathFromCommandLineIfSpecified(string[] args)
        {
            return args.Length == 1 ? args[0] : null;
        }

        private static async Task CreateNamedPipe(Example example)
        {
            Console.WriteLine("Creating Pipe....");
            while (true)
            {
                using (NamedPipeServerStream pipeServer = new NamedPipeServerStream("SLClientPipe", PipeDirection.InOut))
                {
                    pipeServer.WaitForConnection();

                    using (StreamReader reader = new StreamReader(pipeServer))
                    {

                        try
                        {
                            // Read data from the named pipe
                            string message = reader.ReadLine();

                            Dictionary<string, Action> actions = new Dictionary<string, Action>()
                            {
                                { "Start" , () => example.OnRunSequenceFileButtonClick(null, null) },
                                { "Resume" ,() => example.OnResumeButtonClick(null, null) },
                                { "Pause" , ()=> example.OnBreakButtonClick(null, null)},
                                { "Terminate" , ()=>example.OnTerminateButtonClick(null, null)},
                            };

                            Console.WriteLine("Pipe Message : {0}", message);
                            if (actions.ContainsKey(message))
                            {
                                actions[message].Invoke();
                            }
                            else
                            {
                                Console.WriteLine("Invalid pipe message");
                            }

                        }
                        catch (IOException ex)
                        {
                            // Handle IOException (pipe is broken or closed)
                            Console.WriteLine("Error reading from the named pipe: " + ex.Message);
                        }
                    }
                }
            }
        }
    }
}