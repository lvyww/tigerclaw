#pragma once

#include <stdint.h>

void *engine_create(void);
void engine_destroy(void *handle);
int32_t engine_process_key(void *handle, const char *key_utf8);
char *engine_get_preedit(void *handle);
char *engine_get_commit(void *handle);
void engine_free(char *text);
