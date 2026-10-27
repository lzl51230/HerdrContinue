// Compile as an executable named `codex` for Herdr's foreground identity check.
// This fixture is restricted to isolated watchdog-qa- sessions.
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <time.h>

static void report(const char *state) {
    const char *path = getenv("HERDR_SOCKET_PATH"), *pane = getenv("HERDR_PANE_ID");
    if (!path || !pane || !strstr(path, "watchdog-qa-")) exit(2);
    int fd = socket(AF_UNIX, SOCK_STREAM, 0);
    struct sockaddr_un address = {0};
    address.sun_family = AF_UNIX;
    snprintf(address.sun_path, sizeof(address.sun_path), "%s", path);
    if (connect(fd, (struct sockaddr *)&address, sizeof(address))) exit(3);
    char message[1024];
    int size = snprintf(message, sizeof(message), "{\"id\":\"qa\",\"method\":\"pane.report_agent\",\"params\":{\"pane_id\":\"%s\",\"source\":\"watchdog-qa\",\"agent\":\"codex\",\"state\":\"%s\"}}\n", pane, state);
    write(fd, message, size);
    char response[4096] = {0};
    read(fd, response, sizeof(response) - 1);
    if (strstr(response, "\"error\"")) { fprintf(stderr, "%s\n", response); exit(4); }
    close(fd);
}

int main(void) {
    if (!getenv("HERDR_SOCKET_PATH") || !strstr(getenv("HERDR_SOCKET_PATH"), "watchdog-qa-")) return 2;
    printf("\033[2J\033[H› Isolated watchdog test\n\n■ Selected model is at capacity. Please try a different model.\n\n› Ask Codex to do anything\n");
    fflush(stdout);
    report("idle");
    struct timespec started, ended;
    clock_gettime(CLOCK_MONOTONIC, &started);
    char line[1024];
    if (!fgets(line, sizeof(line), stdin)) return 5;
    clock_gettime(CLOCK_MONOTONIC, &ended);
    FILE *file = fopen("/tmp/herdr-watchdog-qa-input.json", "w");
    fprintf(file, "{\"receivedContinueAndNewline\":%s,\"elapsed\":%.3f,\"count\":1}\n",
            strcmp(line, "继续\n") == 0 ? "true" : "false",
            (ended.tv_sec - started.tv_sec) + (ended.tv_nsec - started.tv_nsec) / 1e9);
    fclose(file);
    report("working");
    printf("\033[2J\033[H• QA agent received the continuation and resumed.\n"); fflush(stdout);
    sleep(1);
    printf("\n› Ask Codex to do anything\n"); fflush(stdout);
    report("idle");
    while (fgets(line, sizeof(line), stdin)) {
        file = fopen("/tmp/herdr-watchdog-qa-input.json", "a");
        fprintf(file, "UNEXPECTED_DUPLICATE\n"); fclose(file);
    }
    return 0;
}
