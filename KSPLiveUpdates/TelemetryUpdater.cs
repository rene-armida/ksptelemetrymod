using IniParser;
using IniParser.Model;
using KerbalEngineer.Flight.Readouts.Body;
// see https://github.com/jrbudda/KerbalEngineer/tree/master/KerbalEngineer/Flight/Readouts/Vessel
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
using static UnityEngine.TouchScreenKeyboard;

namespace KSPLiveTelemetry
{
    // raised to indicate that the game communicated via shared mem that we are shutting down
    internal class ShutdownRequest : Exception
    { }

    // raised to indicate we successfully read from shared memory but got a flag left by the initializiation procedure; 
    // i.e. nothing has been written by the game yet
    internal class AwaitingData : Exception
    { }

    internal class UpdateData
    {
        public string Status;
        public string BodyName;
        public double DVLastStage;
        public Vessel.Situations Situation; // enum, see https://anatid.github.io/XML-Documentation-for-the-KSP-API/class_vessel.html#acea90b73d9b4523383503ec82f2f63bb
        public double StaticPressure; // for graphing, presumably atm pressure
        public Guid VesselId; // changes on undocking, but gives us clues into the identity of the ship
        public string VesselName; // matches vessel -> mission
        public double Pitch; // part of AttitudeProcessor https://github.com/jrbudda/KerbalEngineer/blob/master/KerbalEngineer/Flight/Readouts/Vessel/AttitudeProcessor.cs
        public double Roll; // see above
        public double Heading; // see above
        public float AltitudeTerrain;
        public double AltitudeSealevel;

        public double VelocityH;
        public double VelocityV;
        public double VelocityObt;
        public double VelocitySrf;


        public string JSON ()
        {
            return @"{" +
                @"""irl_time"": """ + DateTime.UtcNow.ToString("o") +
                @""", ""status"": """ + Status +
                @""", ""soi_name"": """ + BodyName +
                @""", ""dv_last_stage"": " + Convert.ToInt64(DVLastStage).ToString() +
                @", ""situation"": """ + Situation +
                @""", ""static_pressure"": " + StaticPressure +
                @", ""vessel_id"": """ + VesselId +
                @", ""vessel_name"": """ + VesselName +
                @""", ""pitch"": " + Pitch +
                @", ""roll"": " + Roll +
                @", ""heading"": " + Heading +
                @", ""altitude_sea_level"": " + AltitudeSealevel +
                @", ""altitude_terrain"": " + AltitudeTerrain +
                @", ""velocity_h"": " + VelocityH +
                @", ""velocity_v"": " + VelocityV +
                @", ""velocity_orbital"": " + VelocityObt +
                @", ""velocity_srf"": " + VelocitySrf +
                @"}";
        }
    }

    internal class UpdateEnvelope
    {
        public DateTime Date;
        public string UpdateJSON;
    }


    internal class TelemetryUpdater
    {
        // when did we last update the game data in shared memory?
        public static DateTime LastSharedMemWrite;
        // how long, in ms, between updates to game data for reading by HTTP thread?
        public static TimeSpan GameDataInterval;
        // how long, in ms, between HTTP POSTs to the backend?
        public static TimeSpan HTTPSendInterval;
        // shared memory, written by the game / plugin, read by the HTTP thread
        public static MemoryMappedFile MemMap;
        // http endpoint address for the backend app interpreting the data. See "kspoverlay"
        public static string Endpoint;
        // thread to read shared memory and send HTTP posts to backend
        public Thread HttpThread;

        public TelemetryUpdater()
        {
            // find configuration
            string configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".kspoverlayupdater.ini");

            FileIniDataParser configParser = new();
            IniData configData = configParser.ReadFile(configPath);
            GameDataInterval = TimeSpan.FromMilliseconds(Int32.Parse(configData["KSPOverlayUpdater"]["GameDataInterval"]));
            HTTPSendInterval = TimeSpan.FromMilliseconds(Int32.Parse(configData["KSPOverlayUpdater"]["HTTPSendInterval"]));
            Endpoint = configData["KSPOverlayUpdater"]["Endpoint"];
            if (Endpoint is null)
            {
                throw new Exception("'Endpoint' configuration is invalid");
            }

            MemMap = MemoryMappedFile.CreateOrOpen("KSPLiveUpdates", 1024 * 512); // 512k max

            // create the background thread but do not start it
            HttpThread = new Thread(httpSendLoop);
            HttpThread.Priority = System.Threading.ThreadPriority.BelowNormal;
            HttpThread.IsBackground = true;
        }

        public UpdateData GetUpdateData()
        {
            if (FlightGlobals.ActiveVessel == null)
            {
                throw new Exception(@"KSPLiveTelemetry ERROR: no FlightGlobals.ActiveVessel!");
            }

            var updateData = new UpdateData();
            if (FlightGlobals.ActiveVessel.mainBody == null)
                updateData.BodyName = "N/A";
            else
                updateData.BodyName = FlightGlobals.ActiveVessel.mainBody.bodyDisplayName.LocalizeRemoveGender();

            updateData.DVLastStage = SimulationProcessor.LastStage.deltaV;
            
            updateData.Situation = FlightGlobals.ActiveVessel.situation;
            updateData.StaticPressure = FlightGlobals.ActiveVessel.staticPressurekPa;

            updateData.VesselName = FlightGlobals.ActiveVessel.vesselName;
            updateData.VesselId = FlightGlobals.ActiveVessel.id;

            // attitude stats - see AttitudeProcessor https://github.com/jrbudda/KerbalEngineer/blob/master/KerbalEngineer/Flight/Readouts/Vessel/AttitudeProcessor.cs
            updateData.Pitch = AttitudeProcessor.Pitch;
            updateData.Roll = AttitudeProcessor.Roll;
            updateData.Heading = AttitudeProcessor.Heading;

            // altitude stats
            updateData.AltitudeSealevel = FlightGlobals.ActiveVessel.altitude;
            updateData.AltitudeTerrain = FlightGlobals.ActiveVessel.heightFromTerrain;

            // velocities
            updateData.VelocityV = FlightGlobals.ActiveVessel.verticalSpeed;
            updateData.VelocityH = FlightGlobals.ActiveVessel.horizontalSrfSpeed;
            updateData.VelocityObt = FlightGlobals.ActiveVessel.obt_speed;
            updateData.VelocitySrf = FlightGlobals.ActiveVessel.srfSpeed;

            return updateData;
        }

        // write to shared memory - for access by HTTP sender thread
        // we use the convention of the data being "x" to indicate shutdown
        public void writeShared(String updateData)
        {
            using (var stream = MemMap.CreateViewStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    // TODO check max size limit
                    writer.Write(DateTime.UtcNow.ToBinary());
                    writer.Write(updateData);
                }
            }
        }

        // read from shared memory - for access by http reporter thread
        public UpdateEnvelope readShared()
        {
            using (var stream = MemMap.CreateViewStream())
            {
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    long date = reader.ReadInt64();
                    string json = reader.ReadString();
                    if (json == "x")
                    {
                        // the plugin wants us to shut down
                        throw new ShutdownRequest();
                    }
                    if ( json == "a")
                    {
                        // there's no data from the game yet
                        throw new AwaitingData();
                    }
                    UpdateEnvelope envelope = new UpdateEnvelope();
                    envelope.Date = DateTime.FromBinary(date);
                    envelope.UpdateJSON = json;
                    return envelope;
                }
            }
        }

        // start background thread for sending http requests to the endpoint
        public void launchHttpThread()
        {
            // flag content for "awaiting data"
            writeShared("a");
            // above avoids leftover shutdown flag in reused shared memory buffer;
            // when reused the http thread reads that and exits on first iteration

            HttpThread.Start();
        }

        public void stopHttpThread()
        {
            // special flag content for the sender thread
            //Debug.Log("KSPLiveTelemetry MEM END");
            writeShared("x");
        }

        // blocking method; sleep and periodically send http updates
        // meant to be called from a separate thread
        public void httpSendLoop()
        {
            DateTime lastHttpSend = DateTime.UtcNow - new TimeSpan(0, 0, 5);
            while (true)
            {
                DateTime now = DateTime.UtcNow;
                if (now - lastHttpSend < HTTPSendInterval)
                {
                    //Debug.Log($"KSPLiveTelemetry {now - lastHttpSend} HTTP WAIT");
                    //Console.WriteLine("...not yet time to send");
                    Thread.Sleep(HTTPSendInterval);
                    continue;
                }

                //Debug.Log($"KSPLiveTelemetry {now - lastHttpSend} HTTP SEND");
                //Console.WriteLine("...sending http request");
                try
                {
                    sendHttpRequest();
                }
                catch (AwaitingData)
                {
                    // Http thread outraced the game in writing data; there's nothing to send
                    // let the sleep and timestamp bump below happen
                }
                catch (ShutdownRequest)
                {
                    // plugin is shutting down, got the request via shared memory, no data to send
                    // does mean that there is a delay of up to pollTime before the background thread exits
                    //Debug.Log($"KSPLiveTelemetry {now - lastHttpSend} HTTP EXIT");
                    return;
                }
                catch (Exception exc)
                {
                   Debug.Log($"KSPLiveTelemetry HTTP ERROR {exc.Message}");
                }
                lastHttpSend = now;
                Thread.Sleep(HTTPSendInterval);
            }
        }

        // send game data to the configured endpoint
        public void sendHttpRequest()
        {
            // this can throw ShutdownRequest; let that happen before we start setting up a WebRequest
            UpdateEnvelope envelope = readShared();

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(Endpoint);
            request.Method = "POST";
            request.ContentType = "application/json";

            ASCIIEncoding encoding = new ASCIIEncoding();
            byte[] byte1 = encoding.GetBytes(envelope.UpdateJSON);
            request.ContentLength = byte1.Length;

            using Stream newStream = request.GetRequestStream();
            newStream.Write(byte1, 0, byte1.Length);
        }

        // if it's been longer than MinUpdate, write game data to the shared memory buffer
        public void ConditionallyWriteSharedMem()
        {
            
            DateTime now = DateTime.UtcNow;
            
            if (now - LastSharedMemWrite < GameDataInterval) {
                //Debug.Log($"KSPLiveTelemetry {now - LastSharedMemWrite} MEM WAIT");
                return;
            }


            // bump update time
            LastSharedMemWrite = now;

            try
            {
                //Debug.Log($"KSPLiveTelemetry {now - LastSharedMemWrite} MEM WRITE");
                writeShared(GetUpdateData().JSON());
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
            if (_updater == null)
            {
                throw new Exception("_updater is null!");
            }
            _updater.ConditionallyWriteSharedMem();
        }

        public void Start()
        {
            //Debug.Log("KSPLiveUpdatesAddon->Start");
            _updater.launchHttpThread();
        }

        public void OnDisable()
        {
            //Debug.Log("KSPLiveUpdatesAddon->OnDisable");
            _updater.stopHttpThread();
        }

        //public void OnDestroy()
        //{

        //}
    }
}
