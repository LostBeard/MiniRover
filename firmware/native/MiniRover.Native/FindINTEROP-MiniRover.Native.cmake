#
# Copyright (c) 2026 Todd Tanner (LostBeard) and MiniRover contributors. MIT License.
#
# nf-interpreter interop module for MiniRover.Native (ESP32 functions nanoFramework does not expose). Found through
# -DNF_INTEROP_SEARCH_PATHS=<this folder>; firmware/build-firmware.bat passes it.
#

# sources live next to this module
set(BASE_PATH_FOR_THIS_MODULE ${CMAKE_CURRENT_LIST_DIR})

list(APPEND MiniRover.Native_INCLUDE_DIRS ${PROJECT_SOURCE_DIR}/src/CLR/Core)
list(APPEND MiniRover.Native_INCLUDE_DIRS ${PROJECT_SOURCE_DIR}/src/CLR/Include)
list(APPEND MiniRover.Native_INCLUDE_DIRS ${PROJECT_SOURCE_DIR}/src/HAL/Include)
list(APPEND MiniRover.Native_INCLUDE_DIRS ${PROJECT_SOURCE_DIR}/src/PAL/Include)
list(APPEND MiniRover.Native_INCLUDE_DIRS ${BASE_PATH_FOR_THIS_MODULE})
# esp32-camera + esp_jpeg public headers (the components themselves are added through NF_EXTRA_IDF_COMPONENT_DIRS)
list(APPEND MiniRover.Native_INCLUDE_DIRS ${BASE_PATH_FOR_THIS_MODULE}/../components/esp32-camera/driver/include)
list(APPEND MiniRover.Native_INCLUDE_DIRS ${BASE_PATH_FOR_THIS_MODULE}/../components/esp32-camera/conversions/include)
list(APPEND MiniRover.Native_INCLUDE_DIRS ${BASE_PATH_FOR_THIS_MODULE}/../components/esp_jpeg/include)

set(MiniRover.Native_SRCS
    MiniRover_Native.cpp
    MiniRover_Native_MiniRover_Native_Board_mshl.cpp
    MiniRover_Native_MiniRover_Native_Board.cpp
    MiniRover_Native_MiniRover_Native_Camera_mshl.cpp
    MiniRover_Native_MiniRover_Native_Camera.cpp
)

foreach(SRC_FILE ${MiniRover.Native_SRCS})
    # reset the cached result each time, or find_file reuses the first hit for every file
    unset(MiniRover.Native_SRC_FILE CACHE)
    find_file(MiniRover.Native_SRC_FILE ${SRC_FILE}
        PATHS ${BASE_PATH_FOR_THIS_MODULE}
        NO_DEFAULT_PATH
        CMAKE_FIND_ROOT_PATH_BOTH
    )
    if(NOT MiniRover.Native_SRC_FILE)
        message(FATAL_ERROR "MiniRover.Native: ${SRC_FILE} not found in ${BASE_PATH_FOR_THIS_MODULE}")
    endif()
    list(APPEND MiniRover.Native_SOURCES ${MiniRover.Native_SRC_FILE})
endforeach()

include(FindPackageHandleStandardArgs)
FIND_PACKAGE_HANDLE_STANDARD_ARGS(MiniRover.Native DEFAULT_MSG MiniRover.Native_INCLUDE_DIRS MiniRover.Native_SOURCES)
