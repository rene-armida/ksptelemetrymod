using System;
using System.Threading;
using System.Collections.Generic;
using System.Text;

namespace KSPLiveTelemetry
{
    public static class TestDriver
    {
        public static void mainHttpUpdate()
        {
            Console.WriteLine("about to send data to server");
            TelemetryUpdater updater = new TelemetryUpdater();
            UpdateData ud = new UpdateData();
            ud.BodyName = "Eeloo";
            ud.DVLastStage = 12120.39193;
            updater.writeShared(ud.JSON());
            // updater.sendHttpRequest();
            updater.launchHttpThread();
            Console.WriteLine("main thread sleeping for 2 seconds");
            Thread.Sleep(2000);
            Console.WriteLine("done");
        }

        internal static void Add4Bobcat6Thumper(UpdateData ud)
        {
            for (int i = 0; i < 6; i++)
            {
                EngineSummary engine = new EngineSummary("Bobcat", 273.54, 309.84);
                engine.PropellantNames = ["LiquidFuel", "Oxidizer"];
                ud.Engines.Add(engine);
            }
            for (int i = 0; i < 4; i++)
            {
                EngineSummary engine = new EngineSummary("Thumper", 1104, 245.5);
                engine.PropellantNames = ["SolidFuel"];
                ud.Engines.Add(engine);
            }

            PropellantSummary lfprop = new PropellantSummary("LiquidFuel", 80.65f, 14048.93f);
            ud.Propellants.Add(lfprop);

            PropellantSummary oxprop = new PropellantSummary("Oxidizer", 80.61f, 17548.93f);
            ud.Propellants.Add(oxprop);

            PropellantSummary sfprop = new PropellantSummary("SolidFuel", 45.95f, 28405.93f);
            ud.Propellants.Add(sfprop);
        }

        internal static void Add1Thumper(UpdateData ud)
        {
            EngineSummary engine = new EngineSummary("Thumper", 1104, 245.5);
            engine.PropellantNames = ["SolidFuel"];
            ud.Engines.Add(engine);

            PropellantSummary sfprop = new PropellantSummary("SolidFuel", 45.95f, 28405.93f);
            ud.Propellants.Add(sfprop);
        }

        public static void mainJsonOutput()
        {
            UpdateData ud = new UpdateData();
            ud.Scene = GameScenes.FLIGHT.ToString();
            //ud.Paused = null;
            ud.BodyName = "Eeloo";
            ud.Situation = Vessel.Situations.PRELAUNCH;
            ud.DVLastStage = 12120.39193;
            ud.Heading = 338.4440061279;
            ud.AltitudeSeaLevel = 121.04439724074;
            ud.VelocityH = double.NaN;
            //ud.VelocityV = -0.0622177;

            //Add1Thumper(ud);
            //Add4Bobcat6Thumper(ud);

            Console.WriteLine(ud.JSON());
        }
    }
}
