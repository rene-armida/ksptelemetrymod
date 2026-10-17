using IniParser;
using IniParser.Model;
using KerbalEngineer.Flight;
using KerbalEngineer.Flight.Readouts.Body;
// see https://github.com/jrbudda/KerbalEngineer/tree/master/KerbalEngineer/Flight/Readouts/Vessel
using KerbalEngineer.Flight.Readouts.Vessel;
using KerbalEngineer.Helpers;
using KSP;
using Steamworks;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Linq.Expressions;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using Unity.Collections;
using UnityEngine;
using VehiclePhysics;
using static System.Collections.Specialized.BitVector32;
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

    internal class EngineSummary
    {
        public string Name;
        public double Thrust;
        public double Isp;

        public EngineSummary(string name, double thrust, double isp)
        {
            Name = name;
            Thrust = thrust;
            Isp = isp;
        }
        public List<string> PropellantNames = new List<string>();
    }

    internal class PropellantSummary
    {
        // name of this resource, e.g. "LiquidFuel"
        public string Name;
        // aggregate mass across all tanks
        public double Mass;
        // 0-1 how much is left?
        public float Remain;

        public PropellantSummary(string name, double mass, float remain)
        {
            Name = name;
            Mass = mass;
            Remain = remain;
        }
    }

    internal class UpdateData
    {
        // see KSP.HighLogic.LoadedScene
        public string Scene;
        // true if paused, false otherwise
        public bool? Paused;
        public string BodyName;
        public double DVLastStage = double.NaN;
        public Vessel.Situations Situation; // enum, see https://anatid.github.io/XML-Documentation-for-the-KSP-API/class_vessel.html#acea90b73d9b4523383503ec82f2f63bb
        public double StaticPressure = double.NaN; // for graphing, presumably atm pressure
        public Guid VesselId; // changes on undocking, but gives us clues into the identity of the ship
        public string VesselName; // matches vessel -> mission
        public double Pitch = double.NaN; // part of AttitudeProcessor https://github.com/jrbudda/KerbalEngineer/blob/master/KerbalEngineer/Flight/Readouts/Vessel/AttitudeProcessor.cs
        public double Roll = double.NaN; // see above
        // following aren't in my version of the assembly, maybe they're future changes that didn't get built
        //public float AoA = float.NaN;
        //public float SideSlip = float.NaN;
        public double Heading = double.NaN; // see above
        public double AltitudeTerrain = double.NaN;
        public double AltitudeSeaLevel = double.NaN;

        public double VelocityH = double.NaN;
        public double VelocityV = double.NaN;
        public double VelocityObt = double.NaN;
        public double VelocitySrf = double.NaN;
        public double Acceleration = float.NaN;

        public float TotalMass = float.NaN;
        public double TotalThrust = double.NaN;


        public List<PropellantSummary> Propellants = new List<PropellantSummary>();
        public List<EngineSummary> Engines = new List<EngineSummary>();

        static public string JsonLine(string fieldname, string value, bool final = false)
        {
            string retval = $"\"{fieldname}\": {value}";
            if(!final)
            {
                retval += @", ";
            }
            return retval;
        }

        static public string FloatLine(string fieldname, double? val, bool final = false )
        {
            string fval;
            if (! val.HasValue || Double.IsNaN(val.Value) || Double.IsInfinity(val.Value) )
            {
                fval = "null";
            }
            else
            {
                fval = val.Value.ToString("#.###");
            }

            // why do we still get an empty string sometimes? which other value is possible here?!
            if( fval == "")
            {
                fval = "null";
            }
            return JsonLine(fieldname, fval, final);
        }

        static public string BoolLine(string fieldname, bool ? value, bool final = false)
        {
            string fval;
            if ( ! value.HasValue )
            {
                fval = "null";
            }
            else
            {
                fval = value.Value.ToString().ToLower();
            }
            return JsonLine(fieldname, fval, final);
        }

        static public string StringLine (string fieldname, string ? value, bool final = false)
        {
            string fval;
            if ( value is null || value is "" )
            {
                fval = "\"null\"";
            }
            else
            {
                fval = $"\"{value}\"";
            }
            return JsonLine(fieldname, fval, final);
        }

        public string GetPropellantsJSON()
        {
            string retval = @"[";
            if (Propellants.Count > 0)
            {
                foreach (PropellantSummary propellant in Propellants)
                {
                    retval += "{" +
                        StringLine("name", propellant.Name) +
                        FloatLine("mass", propellant.Mass) +
                        FloatLine("remain", propellant.Remain, true) +
                        "}, ";
                }
                // remove final comma
                retval = retval.Remove(retval.Length - 2);
            }
            retval += @"]";
            return retval;
        }

        public string GetEnginesJSON()
        {
            string retval = @"[";
            if (Engines.Count > 0)
            {
                foreach (EngineSummary engine in Engines)
                {
                    retval += "{" +
                        StringLine("name", engine.Name) +
                        FloatLine("thrust", engine.Thrust) +
                        FloatLine("isp", engine.Isp) +
                        "\"propellants\": [";

                    foreach (String propellantName in engine.PropellantNames)
                    {
                        retval += $"\"{propellantName}\", ";
                    }
                    // remove comma after final propellant name
                    retval = retval.Remove(retval.Length - 2);
                    retval += "]}, ";
                }
                // remove comma after final engine
                retval = retval.Remove(retval.Length - 2);
            }
            retval += @"]";
            return retval;
        }
        

        // we prefer roll-your-own JSON serialization over dependency hell;
        // couldn't find an easy way to get system.text.Json and especially
        // system.ValueTuple over to the KSP environment
        public string JSON ()
        { 
            return @"{" +
                // game state and envelope info
                StringLine("irl_time", DateTime.UtcNow.ToString("o")) +
                StringLine("scene", Scene) +
                BoolLine("paused", Paused) +

                // matcher -> mission info
                StringLine("soi_name", BodyName) +
                StringLine("vessel_id", VesselId.ToString()) +
                StringLine("vessel_name", VesselName) +
                StringLine("situation", Situation.ToString()) +

                // vessel stats
                FloatLine("dv_last_stage", DVLastStage) +
                FloatLine("total_mass", TotalMass) +
                FloatLine("total_thrust", TotalThrust) + 
                FloatLine("acceleration", Acceleration) + 

                // atitude
                FloatLine("pitch", Pitch) +
                FloatLine("roll", Roll) +
                FloatLine("heading", Heading) +

                // altitude
                FloatLine("static_pressure", StaticPressure) +
                FloatLine("altitude_sea_level", AltitudeSeaLevel) +
                FloatLine("altitude_terrain", AltitudeTerrain) +
                FloatLine("velocity_h", VelocityH) +
                FloatLine("velocity_v", VelocityV) +
                FloatLine("velocity_obt", VelocityObt) +
                FloatLine("velocity_srf", VelocitySrf) +

                // propellants
                "\"propellants\": " + GetPropellantsJSON() + ", " +

                // engines
                "\"engines\": " + GetEnginesJSON()  +

                // end
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
        // warn if we take longer than this amount of time, in ms
        public static TimeSpan SlowThresh;
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
            SlowThresh = TimeSpan.FromMilliseconds(Int32.Parse(configData["KSPOverlayUpdater"]["SlowThresh"]));
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
            // game scene and paused/not
            updateData.Scene = HighLogic.LoadedScene.ToString();
            updateData.Paused = (Time.timeScale == 0);

            if (FlightGlobals.ActiveVessel.mainBody == null)
                updateData.BodyName = "N/A";
            else
                updateData.BodyName = FlightGlobals.ActiveVessel.mainBody.bodyDisplayName.LocalizeRemoveGender();


            // vessel stuff
            updateData.DVLastStage = SimulationProcessor.LastStage.deltaV;
            updateData.Situation = FlightGlobals.ActiveVessel.situation;
            updateData.StaticPressure = FlightGlobals.ActiveVessel.staticPressurekPa;
            updateData.VesselName = FlightGlobals.ActiveVessel.vesselName;
            updateData.VesselId = FlightGlobals.ActiveVessel.id;
            updateData.TotalMass = FlightGlobals.ActiveVessel.GetTotalMass();
            // see https://github.com/jrbudda/KerbalEngineer/blob/master/KerbalEngineer/Flight/Readouts/Vessel/Acceleration.cs
            // this can be different than sum of engine's thrusts, because of vectors
            updateData.TotalThrust = SimulationProcessor.LastStage.actualThrust;
            updateData.Acceleration = SimulationProcessor.LastStage.totalMass > 0 ? SimulationProcessor.LastStage.actualThrust / SimulationProcessor.LastStage.totalMass : 0;


            // attitude stats - see AttitudeProcessor https://github.com/jrbudda/KerbalEngineer/blob/master/KerbalEngineer/Flight/Readouts/Vessel/AttitudeProcessor.cs
            updateData.Pitch = AttitudeProcessor.Pitch;
            updateData.Roll = AttitudeProcessor.Roll;
            updateData.Heading = AttitudeProcessor.Heading;
            //updateData.AoA = AttitudeProcessor.AttackAngle;
            //updateData.SideSlip = AttitudeProcessor.SideslipAngle;

            // altitude stats
            updateData.AltitudeSeaLevel = FlightGlobals.ActiveVessel.altitude;
            updateData.AltitudeTerrain = FlightGlobals.ActiveVessel.heightFromTerrain;

            // velocities
            updateData.VelocityV = FlightGlobals.ActiveVessel.verticalSpeed;
            updateData.VelocityH = FlightGlobals.ActiveVessel.horizontalSrfSpeed;
            updateData.VelocityObt = FlightGlobals.ActiveVessel.obt_speed;
            updateData.VelocitySrf = FlightGlobals.ActiveVessel.srfSpeed;

            // engines and propellants
            foreach(Part enginePart in FlightGlobals.ActiveVessel.GetActiveParts())
            {
                ModuleEngines? engineModule = null;
                foreach (PartModule genericModule in enginePart.Modules)
                {
                    if (typeof(ModuleEngines).IsInstanceOfType(genericModule)) {
                        engineModule = (ModuleEngines)genericModule;
                        break;
                    }
                }
                if (engineModule == null)
                {
                    // couldn't find an ModuleEngines attached to the part; 
                    // skip this part, must be something else
                    continue;
                }

                EngineSummary engineSummary = new EngineSummary(
                    enginePart.partInfo.title.Replace("\"", "\\\""),
                    // there are a lot of thrust and isp fields
                    // i've verified that these increase with altitude and decreased pressure, so that seems good
                    engineModule.finalThrust,
                    engineModule.realIsp);                
                List<PropellantSummary> propellantSummaries = new List<PropellantSummary>();

                foreach(Propellant propellant in engineModule.propellants)
                {
                    // add this propellant's name to the EngineSummary
                    engineSummary.PropellantNames.Add(propellant.name);

                    bool foundPropSum = false;
                    foreach(PropellantSummary iPropSum in propellantSummaries)
                    {
                        if(propellant.name == iPropSum.Name)
                        {
                            foundPropSum = true;
                            break;
                        }
                    }

                    if(foundPropSum)
                    {
                        // we've already recorded this propellant in the global list
                        continue;
                    }

                    PropellantSummary propellantSummary = new PropellantSummary(
                        propellant.name, 
                        1, //TODO
                        0.5f); // TODO
                    propellantSummaries.Add(propellantSummary);
                }
                updateData.Engines.Add(engineSummary);
            }

            return updateData;
        }

        // write to shared memory - for access by HTTP sender thread
        // we use the convention of the data being "x" to indicate shutdown
        public void writeShared(String updateData)
        {
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            using (var stream = MemMap.CreateViewStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    // TODO check max size limit
                    writer.Write(DateTime.UtcNow.ToBinary());
                    writer.Write(updateData);
                }
            }

            stopwatch.Stop();
            if ( stopwatch.Elapsed > SlowThresh )
            {
                UnityEngine.Debug.Log($"KSPLiveUpdates MEM WARN elapsed time: {stopwatch.ElapsedMilliseconds} ms");
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
                   UnityEngine.Debug.Log($"KSPLiveTelemetry HTTP ERROR {exc.Message}");
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
            // TODO: add timing; if this method takes longer than X, emit a warning to the debug log
            
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
                UnityEngine.Debug.Log($"KSPLiveTelemetry ERROR {exc.Message}");
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
