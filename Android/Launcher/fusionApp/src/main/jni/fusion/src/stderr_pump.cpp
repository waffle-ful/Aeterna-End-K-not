#include <stderr_pump.h>
#include <blackbox.h>
#include <android/log.h>
#include <fcntl.h>
#include <poll.h>
#include <pthread.h>
#include <sys/ioctl.h>
#include <unistd.h>
#include <atomic>
#include <cerrno>
#include <cstring>
#include <ctime>

// Diagnostics here go straight to logcat: the shared log() could end up writing to the pipe.
#define TAG "FusionStderr"
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, TAG, __VA_ARGS__)

namespace
{
    constexpr size_t MAX_LINE = 480;
    // A line without its line break is passed on after this long without more input. Well
    // under the time stderr_pump_settle is prepared to wait, so that a last line that was not
    // ended still makes it.
    constexpr int PARTIAL_FLUSH_MS = 50;
    constexpr long SETTLE_STEP_NS = 10 * 1000000L;
    constexpr int SETTLE_STEPS = 20;

    std::atomic<bool> started{false};
    std::atomic<int> read_fd{-1};
    std::atomic<pid_t> pump_tid{0};
    // Set before bytes are taken out of the pipe and cleared once they are in the ring, so
    // written text is always visible as either unread bytes or this flag.
    std::atomic<bool> busy{false};

    void emit(char *line, size_t length)
    {
        while (length > 0 && (line[length - 1] == '\r' || line[length - 1] == ' '))
        {
            length--;
        }
        if (length == 0)
        {
            return;
        }

        line[length] = '\0';
        blackbox_write_tagged("stderr", line);
        __android_log_write(ANDROID_LOG_WARN, TAG, line);
    }

    void *run(void *)
    {
        pthread_setname_np(pthread_self(), "fusion-stderr");
        pump_tid.store(gettid(), std::memory_order_relaxed);

        const int fd = read_fd.load(std::memory_order_relaxed);
        char line[MAX_LINE + 1];
        size_t used = 0;
        char chunk[1024];

        while (true)
        {
            pollfd waiting{fd, POLLIN, 0};
            const int ready = TEMP_FAILURE_RETRY(poll(&waiting, 1, used > 0 ? PARTIAL_FLUSH_MS : -1));
            if (ready < 0)
            {
                break;
            }
            if (ready == 0)
            {
                emit(line, used);
                used = 0;
                busy.store(false, std::memory_order_release);
                continue;
            }

            busy.store(true, std::memory_order_release);
            const ssize_t got = TEMP_FAILURE_RETRY(read(fd, chunk, sizeof(chunk)));
            if (got <= 0)
            {
                break;
            }

            for (ssize_t i = 0; i < got; i++)
            {
                const char c = chunk[i];
                if (c == '\n')
                {
                    emit(line, used);
                    used = 0;
                    continue;
                }

                if (used == MAX_LINE)
                {
                    emit(line, used);
                    used = 0;
                }
                line[used++] = c;
            }

            // A line still being collected counts as not yet delivered.
            if (used == 0)
            {
                busy.store(false, std::memory_order_release);
            }
        }

        emit(line, used);
        busy.store(false, std::memory_order_release);
        return nullptr;
    }
}

void stderr_pump_start()
{
    if (started.exchange(true))
    {
        return;
    }

    int fds[2];
    if (pipe2(fds, O_CLOEXEC) != 0)
    {
        LOGW("Standard error is not captured: cannot create the pipe: %s", strerror(errno));
        return;
    }

    // Only the writing end: a writer that finds the pipe full gets an error instead of waiting
    // for this thread, which may be the one that is stuck.
    fcntl(fds[1], F_SETFL, fcntl(fds[1], F_GETFL) | O_NONBLOCK);

    read_fd.store(fds[0], std::memory_order_relaxed);
    pthread_t thread;
    const int error = pthread_create(&thread, nullptr, run, nullptr);
    if (error != 0)
    {
        LOGW("Standard error is not captured: cannot start the thread: %s", strerror(error));
        read_fd.store(-1, std::memory_order_relaxed);
        close(fds[0]);
        close(fds[1]);
        return;
    }
    pthread_detach(thread);

    // Last, so that standard error is only redirected once something is reading the pipe.
    if (dup2(fds[1], STDERR_FILENO) < 0)
    {
        LOGW("Standard error is not captured: cannot redirect it: %s", strerror(errno));
    }
    // Closing this end leaves the copy on standard error as the only writer.
    close(fds[1]);
}

void stderr_pump_settle()
{
    const int fd = read_fd.load(std::memory_order_relaxed);
    if (fd < 0 || gettid() == pump_tid.load(std::memory_order_relaxed))
    {
        return;
    }

    // Done as soon as the pipe is empty and the thread is idle, which is at once when nothing
    // was written.
    for (int step = 0; step < SETTLE_STEPS; step++)
    {
        int unread = 0;
        const bool pending = ioctl(fd, FIONREAD, &unread) == 0 && unread > 0;
        if (!pending && !busy.load(std::memory_order_acquire))
        {
            return;
        }

        timespec pause{0, SETTLE_STEP_NS};
        nanosleep(&pause, nullptr);
    }
}
