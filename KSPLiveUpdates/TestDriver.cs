using System;
using System.Threading;
using System.Collections.Generic;
using System.Text;

namespace KSPLiveUpdates
{
    public static class TestDriver
    {
        public static string datePatt = @"M/d/yyyy hh:mm:ss tt";

        public static void DisplayNow(string title, DateTime inputDt)
        {
            string dtString = inputDt.ToString(datePatt);
            Console.WriteLine("{0} {1}", title, dtString);
        }

        public static void main()
        {
            KSPOverlayUpdater updater = new KSPOverlayUpdater();
            DateTime now;
            while( true )
            {
                now = DateTime.Now;
                DisplayNow("Time: ", now);
                updater.doUpdate();
                
                Thread.Sleep(1000); // wait one second, literally
            }
        }
    }
}
