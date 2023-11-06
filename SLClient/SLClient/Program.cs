using NationalInstruments.TestStand.Grpc.Client.Utilities;

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
            example.OnConnectButtonClick(null, null);
            example.OnRunSequenceFileButtonClick(null, null);
            var parser = new FileParser(
                "C:\\ProgramData\\National Instruments\\Skyline\\TestPlan\\TestSequences\\INI\\TestFile.ini",
                example);

            Console.ReadLine();
        }

        private static string GetConfigFilePathFromCommandLineIfSpecified(string[] args)
        {
            return args.Length == 1 ? args[0] : null;
        }
    }
}