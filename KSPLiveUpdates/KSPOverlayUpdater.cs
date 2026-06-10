using IniParser;
using IniParser.Model;
using KSPLiveUpdates;
//using Microsoft.Extensions.Logging;
using Smooth.Compare;
using System.Net.Http.Headers;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using UnityEngine;


namespace KSPLiveUpdates
{
    internal class KSPOverlayUpdater
    {
        public static DateTime LastUpdate;
        public static TimeSpan MinUpdate;
        private HttpClient client;
        private Uri Endpoint;
        //private Microsoft.Extensions.Logging.ILogger logger;

        public KSPOverlayUpdater()
        {
            //using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddConsole());
            //logger = factory.CreateLogger("Program");

            // find configuration
            string configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".kspoverlayupdater.ini");

            FileIniDataParser configParser = new();
            IniData configData = configParser.ReadFile(configPath);
            // @"C:\Users\rene\.kspoverlayupdater.ini"); //
            MinUpdate = TimeSpan.FromMilliseconds(Int32.Parse(configData["KSPOverlayUpdater"]["MinUpdate"]));
            Endpoint = new Uri(configData["KSPOverlayUpdater"]["Endpoint"]);

            // create HTTP client
            client = new HttpClient();
            client.BaseAddress = Endpoint;
            client.Timeout = TimeSpan.FromMilliseconds(Int32.Parse(configData["KSPOverlayUpdater"]["Timeout"]));
            // Add an Accept header for JSON format.
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public void doUpdate()
        {
            
            DateTime now = DateTime.UtcNow;
            if (now - LastUpdate > MinUpdate)
            {
                // send the request
                try {
                    HttpResponseMessage response = client.GetAsync("/update").Result;  // Blocking call! Program will wait here until a response is received or a timeout occurs.
                    if (response.IsSuccessStatusCode)
                    {
                        // Parse the response body.
                        /*var dataObjects = response.Content.ReadAsAsync<IEnumerable<DataObject>>().Result;  //Make sure to add a reference to System.Net.Http.Formatting.dll
                        foreach (var d in dataObjects)
                        {
                            Console.WriteLine("{0}", d.Name);
                        }*/
                        Console.WriteLine("success");

                    }

                    // bump update time
                    LastUpdate = now;
                }
                catch (Exception exc) {
                    Console.WriteLine("exception caught during http request to {0}", Endpoint);
                }
                

            }
        }
    }



    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class KSPLiveUpdatesAddon : MonoBehaviour
    {
        private KSPOverlayUpdater _updater = new KSPLiveUpdates.KSPOverlayUpdater();

        public void Update()
        {
            _updater.doUpdate();
        }
    }
}
