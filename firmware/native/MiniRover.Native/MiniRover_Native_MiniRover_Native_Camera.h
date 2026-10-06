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

#ifndef MINIROVER_NATIVE_MINIROVER_NATIVE_MINIROVER_NATIVE_CAMERA_H
#define MINIROVER_NATIVE_MINIROVER_NATIVE_MINIROVER_NATIVE_CAMERA_H

namespace MiniRover_Native
{
    namespace MiniRover_Native
    {
        struct Camera
        {
            // Helper Functions to access fields of managed object
            // Declaration of stubs. These functions are implemented by Interop code developers

            static signed int Init( signed int param0, signed int param1, HRESULT &hr );

            static void Stream( signed int param0, signed int param1, signed int param2, HRESULT &hr );

            static bool Configure( signed int param0, signed int param1, HRESULT &hr );

            static void SetOrientation( bool param0, bool param1, HRESULT &hr );

            static signed int GetStat( signed int param0, HRESULT &hr );

        };
    }
}

#endif // MINIROVER_NATIVE_MINIROVER_NATIVE_MINIROVER_NATIVE_CAMERA_H
