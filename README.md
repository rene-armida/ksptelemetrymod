Send telemetry data - altitude, heading, throttle, thrust, engines, propellants - 
via http to a backend service. Tries to minimize the blocking time in the main
game thread by passing data via shared memory to a separate thread.

Big caveat: requires JRBudda's Kerbal Engineer Redux. That's what does the 
heavy lifting in terms of calculating stats. This code just passes data out of
KSP for the backend to use. See https://github.com/jrbudda/KerbalEngineer
