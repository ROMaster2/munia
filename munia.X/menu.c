#include "menu.h"
#include "lcd.h"
#include "globals.h"
#include "snes.h"
#include "n64.h"
#include "gamecube.h"
#include "gamepad.h"
#include <stdbool.h>

#define max(x, y) (((x) > (y)) ? (x) : (y))
#define min(x, y) (((x) < (y)) ? (x) : (y))

uint8_t submenu_idx = 0;
bool menu_leftalign = true;
const uint8_t num_menu_pages = MENU_PAGE_COUNT;
uint8_t menu_next_press_delay;
const char** menu_current_items;
uint8_t submenu_count = 0;

void menu_press(uint8_t command);
void menu_display_setting();

void menu_enter() {
    lcd_command(LCD_DISPLAY_ON_CURSOR_OFF);
    lcd_backLightValue = 10;
    in_menu = true;
    
    // copy config
    memcpy(&config_backup, &config, sizeof(config_t));
    memcpy(&config_edit, &config, sizeof(config_t));
    
    config.ngc_mode = NGC_MODE_PC;
    config.n64_mode = N64_MODE_PC;
    config.snes_mode = SNES_MODE_PC;
    apply_config();
    
    menu_page(MENU_PAGE_NGC);
}
void menu_exit(bool save_settings) {
    lcd_command(LCD_DISPLAY_OFF);
    lcd_backLightValue = 0;
    in_menu = false;
    
    if (save_settings) {
        memcpy(&config, &config_edit, sizeof(config_t));
        save_config();
    }
    else {
        memcpy(&config, &config_backup, sizeof(config_t));
    }
    
    apply_config();
}

void menu_page(uint8_t page) {
    current_menu_page = page;
    menu_current_items = menu_sub_items[current_menu_page];
    submenu_idx = 0;
    menu_leftalign = true;
    submenu_count = 0;
    while (menu_current_items[submenu_count] != NULL) submenu_count++;

    lcd_clear();
    lcd_goto(1, 0);

    if (page == MENU_PAGE_NGC) {
        lcd_string("NGC");
        submenu_idx = config_edit.ngc_mode;
    }
    else if (page == MENU_PAGE_N64) {
        lcd_string("N64"); 
        submenu_idx = config_edit.n64_mode;
    }
    else if (page == MENU_PAGE_SNES) {
        lcd_string("SNES");
        submenu_idx = config_edit.snes_mode;
    }
    else if (page == MENU_PAGE_EXIT) 
        lcd_string("Save");
    lcd_string(" config");
    
    menu_display_setting();
}

void menu_display_setting() {    
    uint8_t left_display = min(submenu_count - 2, max(0, menu_leftalign  ? submenu_idx - 1 : submenu_idx));
    uint8_t right_display = left_display + 1;
        
    lcd_goto(2, 0);
    lcd_char(left_display != 0 ? '<' : ' ');

    for (uint8_t i = left_display; i <= right_display; i++) {
        lcd_string(menu_current_items[i]);
        if (current_menu_page != MENU_PAGE_EXIT) {
            lcd_char('[');
            lcd_char(submenu_idx == i ? '*' : ' ');
            lcd_string("] ");
        }
    }
    
    lcd_goto(2, 15);
    lcd_char(right_display < submenu_count - 1 ? '>' : ' ');    
}


void menu_tasks() {
    if (!in_menu) return;
    if (menu_next_press_delay > 0) { 
        menu_next_press_delay--; 
        return; 
    }
    if (packets.snes_avail) {
        if (joydata_snes.dpad == HAT_SWITCH_WEST) menu_press(mc_left);
        else if (joydata_snes.dpad == HAT_SWITCH_EAST) menu_press(mc_right);
        if (joydata_snes.l) menu_press(mc_prev_page);
        else if (joydata_snes.r) menu_press(mc_next_page);
        else if (joydata_snes.start) menu_press(mc_exit);
        else if (joydata_snes.a) menu_press(mc_confirm);
        else if (joydata_snes.b) menu_press(mc_cancel);
        packets.snes_avail = false;
    }
    if (packets.n64_avail) {
        if (joydata_n64.dpad == HAT_SWITCH_WEST) menu_press(mc_left);
        else if (joydata_n64.dpad == HAT_SWITCH_EAST) menu_press(mc_right);
        if (joydata_n64.l) menu_press(mc_prev_page);
        else if (joydata_n64.r) menu_press(mc_next_page);
        else if (joydata_n64.start) menu_press(mc_exit);
        else if (joydata_n64.a) menu_press(mc_confirm);
        else if (joydata_n64.b) menu_press(mc_cancel);
        packets.n64_avail = false;
    }
    else if (packets.ngc_avail) {
        if (joydata_ngc.hat == HAT_SWITCH_WEST) menu_press(mc_left);
        else if (joydata_ngc.hat == HAT_SWITCH_EAST) menu_press(mc_right);
        if (joydata_ngc.l) menu_press(mc_prev_page);
        else if (joydata_ngc.r) menu_press(mc_next_page);
        else if (joydata_ngc.start) menu_press(mc_exit);
        else if (joydata_ngc.a) menu_press(mc_confirm);
        else if (joydata_ngc.b) menu_press(mc_cancel);
        packets.ngc_avail = false;
    }
}

void menu_press(uint8_t command) {
    menu_next_press_delay = 250; // 250ms
    if (command == mc_prev_page) 
        menu_page((current_menu_page + MENU_PAGE_COUNT - 1) % MENU_PAGE_COUNT);
    else if (command == mc_next_page) 
        menu_page((current_menu_page + 1) % MENU_PAGE_COUNT);
    else if (command == mc_exit)
        menu_page(MENU_PAGE_EXIT);
    else if (command == mc_confirm && current_menu_page == MENU_PAGE_EXIT)
        menu_exit(true);
    else if (command == mc_cancel && current_menu_page == MENU_PAGE_EXIT)
        menu_exit(false);
    
    else if (command == mc_left) {
        if (submenu_idx == submenu_count - 2 && !menu_leftalign) 
            menu_leftalign = true;
        else {
            submenu_idx = max(0, submenu_idx - 1);
            if (current_menu_page == MENU_PAGE_NGC) config_edit.ngc_mode   = submenu_idx;
            if (current_menu_page == MENU_PAGE_N64) config_edit.n64_mode   = submenu_idx;
            if (current_menu_page == MENU_PAGE_SNES) config_edit.snes_mode = submenu_idx;
        }
        menu_display_setting();
    }
    else if (command == mc_right) {
        if (submenu_idx == 1 && menu_leftalign) 
            menu_leftalign = false;
        else {
            submenu_idx = min(submenu_idx + 1, submenu_count - 1);
            if (current_menu_page == MENU_PAGE_NGC) config_edit.ngc_mode   = submenu_idx;
            if (current_menu_page == MENU_PAGE_N64) config_edit.n64_mode   = submenu_idx;
            if (current_menu_page == MENU_PAGE_SNES) config_edit.snes_mode = submenu_idx;
        }        
        menu_display_setting();    
    }
}
