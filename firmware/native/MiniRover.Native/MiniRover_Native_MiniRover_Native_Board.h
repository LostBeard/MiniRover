//-----------------------------------------------------------------------------
//
//                   ** WARNING! ** 
//    This file was generated automatically by a tool.
//    Re-running the tool will overwrite this file.
//    You should copy this file to a custom location
//    before adding any customization in the copy to
//    prevent loss of your changes when the tool is
//    re-run.
//
//-----------------------------------------------------------------------------

#ifndef MINIROVER_NATIVE_MINIROVER_NATIVE_MINIROVER_NATIVE_BOARD_H
#define MINIROVER_NATIVE_MINIROVER_NATIVE_MINIROVER_NATIVE_BOARD_H

namespace MiniRover_Native
{
    namespace MiniRover_Native
    {
        struct Board
        {
            // Helper Functions to access fields of managed object
            // Declaration of stubs. These functions are implemented by Interop code developers

            static signed int WifiRssi(  HRESULT &hr );

            static bool SetWifiPowerSave( bool param0, HRESULT &hr );

            static signed int FreeMemory( signed int param0, HRESULT &hr );

            static signed int ResetReason(  HRESULT &hr );

            static signed int AdcMillivolts( signed int param0, HRESULT &hr );

            static bool BluetoothOff(  HRESULT &hr );

            static bool UdpTest( const char* param0, signed int param1, signed int param2, HRESULT &hr );

            static void FullReset(  HRESULT &hr );

        };
    }
}

#endif // MINIROVER_NATIVE_MINIROVER_NATIVE_MINIROVER_NATIVE_BOARD_H
