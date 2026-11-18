# Nova Elite onboard EQ read

The Onboard EQ menu is shown only while a Nova Elite (`1038:2244` or `1038:2270`) is connected. Arctis 7+ headsets do not get the menu.

Reading is an interrupt write of `01 20` followed by `HidD_GetFeature` on usage page `0xFFC0`. On Windows the returned buffer has the report id removed, so byte 0 is `0x20`. After that: 10 wireless bands, 10 measured bands, 10 Bluetooth gains, 10 microphone gains. Each wireless band is 6 bytes: frequency `uint16` little-endian, filter type, signed gain in tenths of a dB, Q `uint16` little-endian in thousandths. Preset indexes are at bytes 148, 149, and 150.

The editor writes only wireless slot 4, the Custom slot, with little-endian frequencies. That write makes the slot active. It does not write microphone or Bluetooth EQ, and it does not send `01 09`, so the curve is not stored in flash. Arctis 7+ never sees this command.
