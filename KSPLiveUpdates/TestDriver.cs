using Smooth.Algebraics;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace KSPLiveTelemetry
{
    public static class TestDriver
    {
        public static void mainHttpUpdater()
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

        public static void main()
        {
            Console.WriteLine("let's test out some JSON serialization!");
            UpdateData ud = new UpdateData();
            ud.BodyName = "Eeloo";
            ud.Situation = Vessel.Situations.PRELAUNCH;
            ud.DVLastStage = 12120.39193;
            ud.Heading = 338.4440061279;
            ud.AltitudeSealevel = 121.04439724074;
            ud.VelocityV = -0.0622177;

            Console.WriteLine(ud.JSON());
        }
    }
}
