using IniParser;
using IniParser.Model;
using KerbalEngineer.Flight.Readouts.Body;
using KerbalEngineer.Flight.Readouts.Vessel;
using KSP;
using Steamworks;
using System.ComponentModel;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Linq.Expressions;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using UnityEngine;
using VehiclePhysics;

namespace KSPLiveTelemetry
{
    internal class UpdateData
    {
        public string BodyName;
        public double DVLastStage;

        public string JSON ()
        {
            return @"{""BodyName"": """ + BodyName +
                @""", ""DVLastStage"": " + Convert.ToInt64(DVLastStage).ToString() +
                @"}";
        }
    }


    internal class TelemetryUpdater
    {
        public static DateTime LastUpdate;
        public static TimeSpan MinUpdate;
        public static MemoryMappedFile MemMap;
        public static string Endpoint;

        public TelemetryUpdater()
        {
            // find configuration
            string configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".kspoverlayupdater.ini");

            FileIniDataParser configParser = new();
            IniData configData = configParser.ReadFile(configPath);
            MinUpdate = TimeSpan.FromMilliseconds(Int32.Parse(configData["KSPOverlayUpdater"]["MinUpdate"]));
            Endpoint = configData["KSPOverlayUpdater"]["Endpoint"];
            if( Endpoint is null )
            {
                throw new Exception("'Endpoint' configuration is invalid");
            }

            MemMap = MemoryMappedFile.CreateNew("KSPLiveUpdates", 1024 * 512); // 512k max limit
        }
        
        public UpdateData GetUpdateData()
        {
            var updateData = new UpdateData();
            if (FlightGlobals.ActiveVessel.mainBody == null)
                updateData.BodyName = "N/A";
            else
                updateData.BodyName = FlightGlobals.ActiveVessel.mainBody.bodyDisplayName.LocalizeRemoveGender();
            
            updateData.DVLastStage = SimulationProcessor.LastStage.deltaV;

            return updateData;
        }

        // write to shared memory - for access by another thread
        public void writeShared(UpdateData updateData)
        {
            using (var stream = MemMap.CreateViewStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    // TODO check max size limit
                    writer.Write(updateData.JSON());
                }
            }
        }

        // read from shared memory - for access by http reporter thread
        public string readShared()
        {
            using (var stream = MemMap.CreateViewStream())
            {
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    string json = reader.ReadString();
                    return json;
                }
            }
        }

        // start background thread for sending http requests to the endpoint
        public void launchPollThread()
        {
            Thread thread = new Thread(httpSendLoop);
            thread.Priority = System.Threading.ThreadPriority.BelowNormal;
            thread.IsBackground = true;
            thread.Start();
        }

        // blocking method; sleep and periodically send http updates
        // meant to be called from a separate thread
        public void httpSendLoop()
        {
            DateTime lastHttpSend = DateTime.UtcNow - new TimeSpan(0, 0, 5);
            // TODO: configurable polling time
            TimeSpan pollTime = new TimeSpan(0, 0, 0, 0, 200); // 200 ms
            while (true)
            {
                DateTime now = DateTime.UtcNow;
                if (now - lastHttpSend < pollTime)
                {
                    Console.WriteLine("...not yet time to send");
                    Thread.Sleep(pollTime);
                    continue;
                }

                Console.WriteLine("...sending http request");
                sendHttpRequest();
                LastUpdate = now;
                Thread.Sleep(pollTime);
            }
        }

        // send game data to the configured endpoint
        public void sendHttpRequest()
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(Endpoint);
            request.Method = "POST";
            request.ContentType = "application/json";

            string postData = @"{""date"": """ + DateTime.UtcNow.ToString("o") +
                @""", ""status"": ""paused"", ""data"": " + readShared() +
                @"}";
            ASCIIEncoding encoding = new ASCIIEncoding();
            byte[] byte1 = encoding.GetBytes(postData);
            request.ContentLength = byte1.Length;

            using Stream newStream = request.GetRequestStream();
            newStream.Write(byte1, 0, byte1.Length);
        }

        public void ConditionallySendUpdate()
        {
            
            DateTime now = DateTime.UtcNow;
            if (now - LastUpdate < MinUpdate) {
                return;
            }


            // bump update time
            LastUpdate = now;

            try
            {
                writeShared(GetUpdateData());
            }
            catch (Exception exc)
            {
                Debug.Log($"KSPLiveTelemetry ERROR {exc.Message}");
            }

        }
    }

    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class KSPLiveUpdatesAddon : MonoBehaviour
    {
        private TelemetryUpdater _updater = new TelemetryUpdater();

        public void Update()
        {
            _updater.ConditionallySendUpdate();
        }
    }
}
