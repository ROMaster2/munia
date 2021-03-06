#ifndef GLOBALS_H
#define	GLOBALS_H

#include <stdint.h>
#include <stdbool.h>
#include <usb/usb_device.h>

#define FW_MAJOR 1
#define FW_MINOR 7

#define USB_READY (USBDeviceState >= CONFIGURED_STATE && !USBSuspendControl)

bool pollNeeded = false;
extern void load_config();
extern void save_config();
extern void apply_config();
        
USB_HANDLE USBInHandleSNES = 0;
USB_HANDLE USBInHandleN64 = 0;
USB_HANDLE USBInHandleNGC = 0;
USB_HANDLE USBOutHandleCfg = 0;
USB_HANDLE USBInHandleCfg = 0;

char usbOutBuffer[32] @ 0x512; // placed behind joydata_snes
char usbInBuffer[32] @ 0x532;
uint32_t deviceID;

#endif	/* GLOBALS_H */