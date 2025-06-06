include(CheckCXXSourceCompiles)
include(CheckCXXSourceRuns)
include(CheckCXXSymbolExists)
include(CheckFunctionExists)
include(CheckPrototypeDefinition)
include(CheckIncludeFiles)
include(CheckStructHasMember)
include(CheckTypeSize)
include(CheckLibraryExists)

if(CLR_CMAKE_TARGET_FREEBSD)
  set(CMAKE_REQUIRED_INCLUDES ${CROSS_ROOTFS}/usr/local/include)
elseif(CLR_CMAKE_TARGET_SUNOS)
  set(CMAKE_REQUIRED_INCLUDES /opt/local/include)
endif()

if(CLR_CMAKE_TARGET_APPLE)
  set(CMAKE_REQUIRED_DEFINITIONS -D_XOPEN_SOURCE)
elseif(NOT CLR_CMAKE_TARGET_FREEBSD AND NOT CLR_CMAKE_TARGET_NETBSD)
  set(CMAKE_REQUIRED_DEFINITIONS "-D_BSD_SOURCE -D_SVID_SOURCE -D_DEFAULT_SOURCE -D_POSIX_C_SOURCE=200809L")
endif()

if(CLR_CMAKE_TARGET_LINUX AND NOT CLR_CMAKE_TARGET_ANDROID)
  set(CMAKE_RT_LIBS rt)
elseif(CLR_CMAKE_TARGET_FREEBSD OR CLR_CMAKE_TARGET_NETBSD)
  set(CMAKE_RT_LIBS rt)
else()
  set(CMAKE_RT_LIBS "")
endif()

list(APPEND CMAKE_REQUIRED_DEFINITIONS -D_FILE_OFFSET_BITS=64)

check_include_files(sys/vmparam.h HAVE_SYS_VMPARAM_H)
check_include_files(mach/vm_types.h HAVE_MACH_VM_TYPES_H)
check_include_files(mach/vm_param.h HAVE_MACH_VM_PARAM_H)
check_include_files("sys/param.h;sys/types.h;machine/npx.h" HAVE_MACHINE_NPX_H)
check_include_files("sys/param.h;sys/cdefs.h;machine/reg.h" HAVE_MACHINE_REG_H)
check_include_files(machine/vmparam.h HAVE_MACHINE_VMPARAM_H)
check_include_files(procfs.h HAVE_PROCFS_H)
check_include_files(crt_externs.h HAVE_CRT_EXTERNS_H)
check_include_files(sys/time.h HAVE_SYS_TIME_H)
check_include_files(pthread_np.h HAVE_PTHREAD_NP_H)
check_include_files(sys/membarrier.h HAVE_SYS_MEMBARRIER_H)
check_include_files(sys/lwp.h HAVE_SYS_LWP_H)
check_include_files(lwp.h HAVE_LWP_H)
check_include_files(runetype.h HAVE_RUNETYPE_H)
check_include_files(semaphore.h HAVE_SEMAPHORE_H)
check_include_files(sys/prctl.h HAVE_PRCTL_H)
check_include_files("sys/auxv.h;asm/hwcap.h" HAVE_AUXV_HWCAP_H)
check_include_files("sys/ptrace.h" HAVE_SYS_PTRACE_H)
check_include_files("sys/ucontext.h" HAVE_SYS_UCONTEXT_H)
check_include_files("sys/mount.h" HAVE_SYS_MOUNT_H)
check_include_files(ucontext.h HAVE_UCONTEXT_H)
check_symbol_exists(getauxval sys/auxv.h HAVE_GETAUXVAL)

set(CMAKE_REQUIRED_LIBRARIES ${CMAKE_DL_LIBS})

check_cxx_source_compiles("
#include <sys/mman.h>
int main()
{
  return VM_FLAGS_SUPERPAGE_SIZE_ANY;
}
" HAVE_VM_FLAGS_SUPERPAGE_SIZE_ANY)

check_cxx_source_compiles("
#include <sys/mman.h>
int main()
{
  return MAP_HUGETLB;
}
" HAVE_MAP_HUGETLB)

check_cxx_source_compiles("
#include <lttng/tracepoint.h>
int main(int argc, char **argv) {
  return 0;
}" HAVE_LTTNG_TRACEPOINT_H)

set(CMAKE_REQUIRED_LIBRARIES)

check_function_exists(sysctlbyname HAVE_SYSCTLBYNAME)
check_include_files(gnu/lib-names.h HAVE_GNU_LIBNAMES_H)

if(CLR_CMAKE_TARGET_HAIKU)
  # kqueue is broken on Haiku and does not provide the required information in the data field.
  set(HAVE_KQUEUE 0)
else()
  check_function_exists(kqueue HAVE_KQUEUE)
endif()

check_library_exists(c sched_getaffinity "" HAVE_SCHED_GETAFFINITY)
check_library_exists(c sched_setaffinity "" HAVE_SCHED_SETAFFINITY)
check_library_exists(pthread pthread_create "" HAVE_LIBPTHREAD)
check_library_exists(c pthread_create "" HAVE_PTHREAD_IN_LIBC)

if (HAVE_LIBPTHREAD)
  set(PTHREAD_LIBRARY pthread)
elseif (HAVE_PTHREAD_IN_LIBC)
  set(PTHREAD_LIBRARY c)
endif()

check_library_exists(${PTHREAD_LIBRARY} pthread_attr_get_np "" HAVE_PTHREAD_ATTR_GET_NP)
check_library_exists(${PTHREAD_LIBRARY} pthread_getattr_np "" HAVE_PTHREAD_GETATTR_NP)
check_library_exists(${PTHREAD_LIBRARY} pthread_getcpuclockid "" HAVE_PTHREAD_GETCPUCLOCKID)
check_library_exists(${PTHREAD_LIBRARY} pthread_getaffinity_np "" HAVE_PTHREAD_GETAFFINITY_NP)

check_function_exists(fsync HAVE_FSYNC)
check_function_exists(futimes HAVE_FUTIMES)
if(CLR_CMAKE_TARGET_LINUX)
  # sysctl is deprecated on Linux
  set(HAVE_SYSCTL 0)
else()
  check_function_exists(sysctl HAVE_SYSCTL)
endif()
check_function_exists(sysconf HAVE_SYSCONF)
check_function_exists(gmtime_r HAVE_GMTIME_R)
check_function_exists(poll HAVE_POLL)
check_function_exists(statvfs HAVE_STATVFS)
check_function_exists(thread_self HAVE_THREAD_SELF)
check_function_exists(_lwp_self HAVE__LWP_SELF)
check_function_exists(pthread_mach_thread_np HAVE_MACH_THREADS)
check_cxx_source_compiles("
#include <mach/mach.h>
int main(int argc, char **argv) {
  static mach_port_name_t port;
  thread_set_exception_ports(mach_thread_self(), EXC_MASK_BAD_ACCESS, port, EXCEPTION_DEFAULT, MACHINE_THREAD_STATE);
  return 0;
}" HAVE_MACH_EXCEPTIONS)
check_cxx_source_compiles("
#include <signal.h>
#include <stdlib.h>
int main(int argc, char **argv) {
  sigaltstack(NULL, NULL);
  return 0;
}" HAVE_SIGALTSTACK)
check_function_exists(vm_allocate HAVE_VM_ALLOCATE)
check_function_exists(vm_read HAVE_VM_READ)
check_function_exists(directio HAVE_DIRECTIO)
check_function_exists(semget HAS_SYSV_SEMAPHORES)
check_function_exists(pthread_mutex_init HAS_PTHREAD_MUTEXES)
check_function_exists(ttrace HAVE_TTRACE)
check_function_exists(pipe2 HAVE_PIPE2)
check_function_exists(strerrorname_np HAVE_STRERRORNAME_NP)

check_cxx_source_compiles("
#include <pthread_np.h>
int main(int argc, char **argv) {
  cpuset_t cpuSet;

  return 0;
}" HAVE_CPUSET_T)

check_struct_has_member ("struct stat" st_atimespec "sys/types.h;sys/stat.h" HAVE_STAT_TIMESPEC)
check_struct_has_member ("struct stat" st_atim "sys/types.h;sys/stat.h" HAVE_STAT_TIM)
check_struct_has_member ("struct stat" st_atimensec "sys/types.h;sys/stat.h" HAVE_STAT_NSEC)
check_struct_has_member ("ucontext_t" uc_mcontext.gregs[0] ucontext.h HAVE_GREGSET_T)
check_struct_has_member ("ucontext_t" uc_mcontext.__gregs[0] ucontext.h HAVE___GREGSET_T)
check_struct_has_member ("ucontext_t" uc_mcontext.fpregs->__glibc_reserved1[0] ucontext.h HAVE_FPSTATE_GLIBC_RESERVED1)
check_struct_has_member ("struct _fpchip_state" cw sys/ucontext.h HAVE_FPREGS_WITH_CW)

set(CMAKE_EXTRA_INCLUDE_FILES machine/reg.h)
check_type_size("struct reg" BSD_REGS_T)
set(CMAKE_EXTRA_INCLUDE_FILES)
set(CMAKE_EXTRA_INCLUDE_FILES asm/ptrace.h)
check_type_size("struct pt_regs" PT_REGS)
set(CMAKE_EXTRA_INCLUDE_FILES)
if(HAVE_UCONTEXT_H)
  set(CMAKE_EXTRA_INCLUDE_FILES ucontext.h)
else()
  set(CMAKE_EXTRA_INCLUDE_FILES signal.h)
endif()
check_type_size(ucontext_t UCONTEXT_T)
set(CMAKE_EXTRA_INCLUDE_FILES)
set(CMAKE_EXTRA_INCLUDE_FILES pthread.h)
check_type_size(pthread_rwlock_t PTHREAD_RWLOCK_T)
set(CMAKE_EXTRA_INCLUDE_FILES)
set(CMAKE_EXTRA_INCLUDE_FILE procfs.h)
check_type_size(prwatch_t PRWATCH_T)
set(CMAKE_EXTRA_INCLUDE_FILE)
check_type_size(off_t SIZEOF_OFF_T)

check_cxx_symbol_exists(SYS_yield sys/syscall.h HAVE_YIELD_SYSCALL)
check_cxx_symbol_exists(INFTIM poll.h HAVE_INFTIM)
check_cxx_symbol_exists(_DEBUG sys/user.h USER_H_DEFINES_DEBUG)
check_cxx_symbol_exists(_SC_PHYS_PAGES unistd.h HAVE__SC_PHYS_PAGES)
check_cxx_symbol_exists(_SC_AVPHYS_PAGES unistd.h HAVE__SC_AVPHYS_PAGES)

check_cxx_source_runs("
#include <sys/param.h>
#include <stdlib.h>

int main(void) {
  char *path;
#ifdef PATH_MAX
  char resolvedPath[PATH_MAX];
#elif defined(MAXPATHLEN)
  char resolvedPath[MAXPATHLEN];
#else
  char resolvedPath[1024];
#endif
  path = realpath(\"a_nonexistent_file\", resolvedPath);
  if (path == NULL) {
    exit(1);
  }
  exit(0);
}" REALPATH_SUPPORTS_NONEXISTENT_FILES)
check_cxx_source_runs("
#include <stdio.h>
#include <stdlib.h>
#include <fcntl.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <sys/time.h>
#include <unistd.h>

int main(void) {
    int fd, numFDs;
    fd_set readFDs, writeFDs, exceptFDs;
    struct timeval time = { 0 };
    char * filename = NULL;

    filename = (char *)malloc(L_tmpnam * sizeof(char)); /* ok to leak this at exit */
    if (NULL == filename) {
      exit(0);
    }

    /* On some platforms (e.g. HP-UX) the multithreading c-runtime does not
       support the tmpnam(NULL) semantics, and it returns NULL. Therefore
       we need to use the tmpnam(pbuffer) version.
    */
    if (NULL == tmpnam(filename)) {
      exit(0);
    }
    if (mkfifo(filename, S_IRWXU) != 0) {
      if (unlink(filename) != 0) {
        exit(0);
      }
      if (mkfifo(filename, S_IRWXU) != 0) {
        exit(0);
      }
    }
    fd = open(filename, O_RDWR | O_NONBLOCK);
    if (fd == -1) {
      exit(0);
    }

    FD_ZERO(&readFDs);
    FD_ZERO(&writeFDs);
    FD_ZERO(&exceptFDs);
    FD_SET(fd, &readFDs);
    numFDs = select(fd + 1, &readFDs, &writeFDs, &exceptFDs, &time);

    close(fd);
    unlink(filename);

    /* numFDs is zero if select() works correctly */
    exit(numFD==0);
}" HAVE_BROKEN_FIFO_SELECT)
check_cxx_source_runs("
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>
#include <fcntl.h>
#include <string.h>
#include <errno.h>
#include <sys/types.h>
#include <sys/event.h>
#include <sys/time.h>
#include <sys/stat.h>

int main(void)
{
  int ikq;
  int iRet;
  int fd;
  struct kevent ke, keChangeList;
  struct timespec ts = { 0, 0 };

  char * filename = NULL;

  filename = (char *)malloc(L_tmpnam * sizeof(char)); /* ok to leak this at exit */
  if (NULL == filename)
  {
    exit(1);
  }

  /* On some platforms (e.g. HP-UX) the multithreading c-runtime does not
     support the tmpnam(NULL) semantics, and it returns NULL. Therefore
     we need to use the tmpnam(pbuffer) version.
  */
  if (NULL == tmpnam(filename)) {
    exit(0);
  }
  if (mkfifo(filename, S_IRWXU) != 0) {
    if (unlink(filename) != 0) {
      exit(0);
    }
    if (mkfifo(filename, S_IRWXU) != 0) {
      exit(0);
    }
  }
  fd = open(filename, O_RDWR | O_NONBLOCK);
  if (fd == -1) {
    exit(0);
  }

  EV_SET(&keChangeList, fd, EVFILT_READ, EV_ADD | EV_CLEAR, 0, 0, NULL);
  ikq = kqueue();
  iRet = kevent(ikq, &keChangeList, 1, &ke, 1, &ts);

  close(fd);
  unlink(filename);

  /* iRet is zero is kevent() works correctly */
  return(iRet==0);
}" HAVE_BROKEN_FIFO_KEVENT)
set(CMAKE_REQUIRED_LIBRARIES pthread)
check_cxx_source_runs("
#include <stdio.h>
#include <stdlib.h>
#include <pthread.h>
#include <sched.h>

int main(void)
{
  int policy;
  struct sched_param schedParam;
  int max_priority;
  int min_priority;

  if (0 != pthread_getschedparam(pthread_self(), &policy, &schedParam))
  {
    exit(1);
  }

  max_priority = sched_get_priority_max(policy);
  min_priority = sched_get_priority_min(policy);

  exit(-1 == max_priority || -1 == min_priority);
}" HAVE_SCHED_GET_PRIORITY)
check_cxx_source_runs("
#include <stdlib.h>
#include <time.h>
#include <sys/time.h>

int main()
{
  int ret;
  struct timeval tv;
  ret = gettimeofday(&tv, NULL);

  exit(ret);
}" HAVE_WORKING_GETTIMEOFDAY)

set(CMAKE_REQUIRED_LIBRARIES ${CMAKE_RT_LIBS})
check_cxx_source_runs("
#include <stdlib.h>
#include <time.h>
#include <sys/time.h>

int main()
{
  int ret;
  struct timespec ts;
  ret = clock_gettime(CLOCK_REALTIME, &ts);

  exit(ret);
}" HAVE_WORKING_CLOCK_GETTIME)
set(CMAKE_REQUIRED_LIBRARIES)

set(CMAKE_REQUIRED_LIBRARIES ${CMAKE_RT_LIBS})
check_cxx_source_runs("
#include <stdlib.h>
#include <time.h>
#include <sys/time.h>

int main()
{
  int ret;
  struct timespec ts;
  ret = clock_gettime(CLOCK_MONOTONIC, &ts);

  exit(ret);
}" HAVE_CLOCK_MONOTONIC)
set(CMAKE_REQUIRED_LIBRARIES)

check_library_exists(${PTHREAD_LIBRARY} pthread_condattr_setclock "" HAVE_PTHREAD_CONDATTR_SETCLOCK)

set(CMAKE_REQUIRED_LIBRARIES ${CMAKE_RT_LIBS})
check_cxx_source_runs("
#include <stdlib.h>
#include <time.h>
#include <sys/time.h>

int main()
{
  int ret;
  struct timespec ts;
  ret = clock_gettime(CLOCK_THREAD_CPUTIME_ID, &ts);

  exit(ret);
}" HAVE_CLOCK_THREAD_CPUTIME)
set(CMAKE_REQUIRED_LIBRARIES)

check_cxx_source_runs("
#include <sys/types.h>
#include <sys/mman.h>
#include <signal.h>
#include <stdlib.h>
#include <unistd.h>

#ifndef MAP_ANON
#define MAP_ANON MAP_ANONYMOUS
#endif

void *handle_signal(int signal) {
  /* If we reach this, we've crashed due to mmap honoring
  PROT_NONE. */
  _exit(1);
}

int main(void) {
  int *ptr;
  struct sigaction action;

  ptr = (int *) mmap(NULL, getpagesize(), PROT_NONE,
                     MAP_ANON | MAP_PRIVATE, -1, 0);
  if (ptr == (int *) MAP_FAILED) {
    exit(0);
  }
  action.sa_handler = &handle_signal;
  action.sa_flags = 0;
  sigemptyset(&action.sa_mask);
  if (sigaction(SIGBUS, &action, NULL) != 0) {
    exit(0);
  }
  if (sigaction(SIGSEGV, &action, NULL) != 0) {
    exit(0);
  }
  /* This will drop us into the signal handler if PROT_NONE
     is honored. */
  *ptr = 123;
  exit(0);
}" MMAP_ANON_IGNORES_PROTECTION)
check_cxx_source_runs("
#include <stdio.h>
#include <unistd.h>
#include <stdlib.h>
#include <string.h>
#include <sys/types.h>
#include <sys/mman.h>
#include <algorithm>

#define MEM_SIZE 1024
#define TEMP_FILE_TEMPLATE \"${CMAKE_BINARY_DIR}${CMAKE_FILES_DIRECTORY}/CMakeTmp/multiplemaptestXXXXXX\"
int main(void)
{
  char * fname;
  int fd;
  int ret;
  void * pAddr0, * pAddr1;

  fname = (char *)malloc(std::max((size_t)MEM_SIZE, sizeof(TEMP_FILE_TEMPLATE)));
  if (!fname)
    exit(1);
  strcpy(fname, TEMP_FILE_TEMPLATE);

  fd = mkstemp(fname);
  if (fd < 0)
    exit(1);

  ret = write (fd, (void *)fname, MEM_SIZE);
  if (ret < 0)
    exit(1);

  pAddr0 = mmap(0, MEM_SIZE, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
  pAddr1 = mmap(0, MEM_SIZE, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);

  /* In theory we should look for (pAddr1 == MAP_FAILED) && (pAddr1 != MAP_FAILED)
   but in case the first test also failed, i.e. we failed to run the test,
   let's assume that the system might not allow multiple shared mapping of the
   same file region in the same process. The code enabled in this case is
   only a fall-back code path. In case the double mmap actually works, virtually
   nothing will change and the normal code path will be executed */
  if (pAddr1 == MAP_FAILED)
    ret = 1;
  else
    ret = 0;

  if (pAddr0)
    munmap (pAddr0, MEM_SIZE);
  if (pAddr1)
    munmap (pAddr1, MEM_SIZE);
  close(fd);
  unlink(fname);
  free(fname);

  exit(ret != 1);
}" ONE_SHARED_MAPPING_PER_FILEREGION_PER_PROCESS)

set(CMAKE_REQUIRED_LIBRARIES pthread)
check_cxx_source_runs("
#include <errno.h>
#include <pthread.h>
#include <stdlib.h>

void *start_routine(void *param) { return NULL; }

int main() {
  int result;
  pthread_t tid;

  errno = 0;
  result = pthread_create(&tid, NULL, start_routine, NULL);
  if (result != 0) {
    exit(1);
  }
  if (errno != 0) {
    exit(0);
  }
  exit(1);
}" PTHREAD_CREATE_MODIFIES_ERRNO)
set(CMAKE_REQUIRED_LIBRARIES)
set(CMAKE_REQUIRED_LIBRARIES pthread)
check_cxx_source_runs("
#include <errno.h>
#include <semaphore.h>
#include <stdlib.h>

int main() {
  int result;
  sem_t sema;

  errno = 50;
  result = sem_init(&sema, 0, 0);
  if (result != 0)
  {
    exit(1);
  }
  if (errno != 50)
  {
    exit(0);
  }
  exit(1);
}" SEM_INIT_MODIFIES_ERRNO)
set(CMAKE_REQUIRED_LIBRARIES)
check_cxx_source_runs("
#include <fcntl.h>
#include <stdlib.h>
#include <stdio.h>
#include <unistd.h>

int main(void) {
  int fd;
#ifdef PATH_MAX
  char path[PATH_MAX];
#elif defined(MAXPATHLEN)
  char path[MAXPATHLEN];
#else
  char path[1024];
#endif

  sprintf(path, \"/proc/%u/ctl\", getpid());
  fd = open(path, O_WRONLY);
  if (fd == -1) {
    exit(1);
  }
  exit(0);
}" HAVE_PROCFS_CTL)
set(CMAKE_REQUIRED_LIBRARIES)
check_cxx_source_runs("
#include <fcntl.h>
#include <stdlib.h>
#include <stdio.h>
#include <unistd.h>

int main(void) {
  int fd;
#ifdef PATH_MAX
  char path[PATH_MAX];
#elif defined(MAXPATHLEN)
  char path[MAXPATHLEN];
#else
  char path[1024];
#endif

  sprintf(path, \"/proc/%u/stat\", getpid());
  fd = open(path, O_RDONLY);
  if (fd == -1) {
    exit(1);
  }
  exit(0);
}" HAVE_PROCFS_STAT)
set(CMAKE_REQUIRED_LIBRARIES)

set(CMAKE_REQUIRED_LIBRARIES ${PTHREAD_LIBRARY})
check_cxx_source_runs("
#include <stdlib.h>
#include <errno.h>
#include <semaphore.h>

int main() {
  sem_t sema;
  if (sem_init(&sema, 0, 0) == -1){
    exit(1);
  }
  exit(0);
}" HAS_POSIX_SEMAPHORES)
set(CMAKE_REQUIRED_LIBRARIES)

set(SYNCHMGR_SUSPENSION_SAFE_CONDITION_SIGNALING 1)
set(ERROR_FUNC_FOR_GLOB_HAS_FIXED_PARAMS 1)

if(NOT CLR_CMAKE_USE_SYSTEM_LIBUNWIND)
  list(INSERT CMAKE_REQUIRED_INCLUDES 0 ${CLR_SRC_NATIVE_DIR}/external/libunwind/include ${CLR_ARTIFACTS_OBJ_DIR}/external/libunwind/include)
endif()

check_c_source_compiles("
#include <libunwind.h>
#include <ucontext.h>
int main(int argc, char **argv)
{
        unw_context_t libUnwindContext;
        ucontext_t uContext;
        libUnwindContext = uContext;
        return 0;
}" UNWIND_CONTEXT_IS_UCONTEXT_T)

check_symbol_exists(unw_get_save_loc libunwind.h HAVE_UNW_GET_SAVE_LOC)
check_symbol_exists(unw_get_accessors libunwind.h HAVE_UNW_GET_ACCESSORS)
check_symbol_exists(unw_get_proc_info_in_range libunwind.h HAVE_GET_PROC_INFO_IN_RANGE)

check_cxx_source_compiles("
#include <libunwind.h>

int main(int argc, char **argv)
{
    int flag = (int)UNW_AARCH64_X19;
    return 0;
}" HAVE_UNW_AARCH64_X19)

if(NOT CLR_CMAKE_USE_SYSTEM_LIBUNWIND)
  list(REMOVE_AT CMAKE_REQUIRED_INCLUDES 0 1)
endif()

check_cxx_source_compiles("
#include <signal.h>

int main(int argc, char **argv)
{
    struct _xstate xstate;
    struct _fpx_sw_bytes bytes;
    return 0;
}" HAVE_PUBLIC_XSTATE_STRUCT)

if(HAVE_PUBLIC_XSTATE_STRUCT)
    check_struct_has_member ("struct _fpx_sw_bytes" xstate_bv "signal.h" HAVE__FPX_SW_BYTES_WITH_XSTATE_BV)
endif()

check_cxx_source_compiles("
#include <sys/prctl.h>

int main(int argc, char **argv)
{
    int flag = (int)PR_SET_PTRACER;
    return 0;
}" HAVE_PR_SET_PTRACER)

set(CMAKE_REQUIRED_LIBRARIES pthread)
check_cxx_source_compiles("
#include <errno.h>
#include <pthread.h>
#include <time.h>

int main()
{
    pthread_mutexattr_t mutexAttributes;
    pthread_mutexattr_init(&mutexAttributes);
    pthread_mutexattr_setpshared(&mutexAttributes, PTHREAD_PROCESS_SHARED);
    pthread_mutexattr_settype(&mutexAttributes, PTHREAD_MUTEX_RECURSIVE);
    pthread_mutexattr_setrobust(&mutexAttributes, PTHREAD_MUTEX_ROBUST);

    pthread_mutex_t mutex;
    pthread_mutex_init(&mutex, &mutexAttributes);

    pthread_mutexattr_destroy(&mutexAttributes);

    struct timespec timeoutTime;
    timeoutTime.tv_sec = 1; // not the right way to specify absolute time, but just checking availability of timed lock
    timeoutTime.tv_nsec = 0;
    pthread_mutex_timedlock(&mutex, &timeoutTime);
    pthread_mutex_consistent(&mutex);

    pthread_mutex_destroy(&mutex);

    int error = EOWNERDEAD;
    error = ENOTRECOVERABLE;
    error = ETIMEDOUT;
    error = 0;
    return error;
}" HAVE_FULLY_FEATURED_PTHREAD_MUTEXES)
set(CMAKE_REQUIRED_LIBRARIES)

if(NOT CLR_CMAKE_HOST_ARCH_ARM AND NOT CLR_CMAKE_HOST_ARCH_ARM64)
  set(CMAKE_REQUIRED_LIBRARIES pthread)
  check_cxx_source_runs("
  // This test case verifies the pthread process-shared robust mutex's cross-process abandon detection. The parent process starts
  // a child process that locks the mutex, the process process then waits to acquire the lock, and the child process abandons the
  // mutex by exiting the process while holding the lock. The parent process should then be released from its wait, be assigned
  // ownership of the lock, and be notified that the mutex was abandoned.

  #include <sys/mman.h>
  #include <sys/time.h>

  #include <errno.h>
  #include <pthread.h>
  #include <stdio.h>
  #include <unistd.h>

  #include <new>
  using namespace std;

  struct Shm
  {
      pthread_mutex_t syncMutex;
      pthread_cond_t syncCondition;
      pthread_mutex_t robustMutex;
      int conditionValue;

      Shm() : conditionValue(0)
      {
      }
  } *shm;

  int GetFailTimeoutTime(struct timespec *timeoutTimeRef)
  {
      int getTimeResult = clock_gettime(CLOCK_REALTIME, timeoutTimeRef);
      if (getTimeResult != 0)
      {
          struct timeval tv;
          getTimeResult = gettimeofday(&tv, NULL);
          if (getTimeResult != 0)
              return 1;
          timeoutTimeRef->tv_sec = tv.tv_sec;
          timeoutTimeRef->tv_nsec = tv.tv_usec * 1000;
      }
      timeoutTimeRef->tv_sec += 30;
      return 0;
  }

  int WaitForConditionValue(int desiredConditionValue)
  {
      struct timespec timeoutTime;
      if (GetFailTimeoutTime(&timeoutTime) != 0)
          return 1;
      if (pthread_mutex_timedlock(&shm->syncMutex, &timeoutTime) != 0)
          return 1;

      if (shm->conditionValue != desiredConditionValue)
      {
          if (GetFailTimeoutTime(&timeoutTime) != 0)
              return 1;
          if (pthread_cond_timedwait(&shm->syncCondition, &shm->syncMutex, &timeoutTime) != 0)
              return 1;
          if (shm->conditionValue != desiredConditionValue)
              return 1;
      }

      if (pthread_mutex_unlock(&shm->syncMutex) != 0)
          return 1;
      return 0;
  }

  int SetConditionValue(int newConditionValue)
  {
      struct timespec timeoutTime;
      if (GetFailTimeoutTime(&timeoutTime) != 0)
          return 1;
      if (pthread_mutex_timedlock(&shm->syncMutex, &timeoutTime) != 0)
          return 1;

      shm->conditionValue = newConditionValue;
      if (pthread_cond_signal(&shm->syncCondition) != 0)
          return 1;

      if (pthread_mutex_unlock(&shm->syncMutex) != 0)
          return 1;
      return 0;
  }

  void DoTest_Child();

  int DoTest()
  {
      // Map some shared memory
      void *shmBuffer = mmap(NULL, 4096, PROT_READ | PROT_WRITE, MAP_ANONYMOUS | MAP_SHARED, -1, 0);
      if (shmBuffer == MAP_FAILED)
          return 1;
      shm = new(shmBuffer) Shm;

      // Create sync mutex
      pthread_mutexattr_t syncMutexAttributes;
      if (pthread_mutexattr_init(&syncMutexAttributes) != 0)
          return 1;
      if (pthread_mutexattr_setpshared(&syncMutexAttributes, PTHREAD_PROCESS_SHARED) != 0)
          return 1;
      if (pthread_mutex_init(&shm->syncMutex, &syncMutexAttributes) != 0)
          return 1;
      if (pthread_mutexattr_destroy(&syncMutexAttributes) != 0)
          return 1;

      // Create sync condition
      pthread_condattr_t syncConditionAttributes;
      if (pthread_condattr_init(&syncConditionAttributes) != 0)
          return 1;
      if (pthread_condattr_setpshared(&syncConditionAttributes, PTHREAD_PROCESS_SHARED) != 0)
          return 1;
      if (pthread_cond_init(&shm->syncCondition, &syncConditionAttributes) != 0)
          return 1;
      if (pthread_condattr_destroy(&syncConditionAttributes) != 0)
          return 1;

      // Create the robust mutex that will be tested
      pthread_mutexattr_t robustMutexAttributes;
      if (pthread_mutexattr_init(&robustMutexAttributes) != 0)
          return 1;
      if (pthread_mutexattr_setpshared(&robustMutexAttributes, PTHREAD_PROCESS_SHARED) != 0)
          return 1;
      if (pthread_mutexattr_setrobust(&robustMutexAttributes, PTHREAD_MUTEX_ROBUST) != 0)
          return 1;
      if (pthread_mutex_init(&shm->robustMutex, &robustMutexAttributes) != 0)
          return 1;
      if (pthread_mutexattr_destroy(&robustMutexAttributes) != 0)
          return 1;

      // Start child test process
      int error = fork();
      if (error == -1)
          return 1;
      if (error == 0)
      {
          DoTest_Child();
          return -1;
      }

      // Wait for child to take a lock
      WaitForConditionValue(1);

      // Wait to try to take a lock. Meanwhile, child abandons the robust mutex.
      struct timespec timeoutTime;
      if (GetFailTimeoutTime(&timeoutTime) != 0)
          return 1;
      error = pthread_mutex_timedlock(&shm->robustMutex, &timeoutTime);
      if (error != EOWNERDEAD) // expect to be notified that the robust mutex was abandoned
          return 1;
      if (pthread_mutex_consistent(&shm->robustMutex) != 0)
          return 1;

      if (pthread_mutex_unlock(&shm->robustMutex) != 0)
          return 1;
      if (pthread_mutex_destroy(&shm->robustMutex) != 0)
          return 1;
      return 0;
  }

  void DoTest_Child()
  {
      // Lock the robust mutex
      struct timespec timeoutTime;
      if (GetFailTimeoutTime(&timeoutTime) != 0)
          return;
      if (pthread_mutex_timedlock(&shm->robustMutex, &timeoutTime) != 0)
          return;

      // Notify parent that robust mutex is locked
      if (SetConditionValue(1) != 0)
          return;

      // Wait a short period to let the parent block on waiting for a lock
      sleep(1);

      // Abandon the mutex by exiting the process while holding the lock. Parent's wait should be released by EOWNERDEAD.
  }

  int main()
  {
      int result = DoTest();
      return result >= 0 ? result : 0;
  }" HAVE_FUNCTIONAL_PTHREAD_ROBUST_MUTEXES)
  set(CMAKE_REQUIRED_LIBRARIES)
endif()

if(CLR_CMAKE_TARGET_APPLE)
  set(HAVE__NSGETENVIRON 1)
  set(DEADLOCK_WHEN_THREAD_IS_SUSPENDED_WHILE_BLOCKED_ON_MUTEX 1)
  set(PAL_PTRACE "ptrace((cmd), (pid), (caddr_t)(addr), (data))")
  set(HAVE_SCHED_OTHER_ASSIGNABLE 1)

elseif(CLR_CMAKE_TARGET_FREEBSD)
  set(DEADLOCK_WHEN_THREAD_IS_SUSPENDED_WHILE_BLOCKED_ON_MUTEX 0)
  set(PAL_PTRACE "ptrace((cmd), (pid), (caddr_t)(addr), (data))")
  if (CLR_CMAKE_HOST_ARCH_AMD64)
    set(BSD_REGS_STYLE "((reg).r_##rr)")
  elseif(CLR_CMAKE_HOST_ARCH_ARM64)
    set(BSD_REGS_STYLE "((reg).rr)")
  else()
    message(FATAL_ERROR "Unknown FreeBSD architecture")
  endif()
  set(HAVE_SCHED_OTHER_ASSIGNABLE 1)
elseif(CLR_CMAKE_TARGET_NETBSD)
  set(DEADLOCK_WHEN_THREAD_IS_SUSPENDED_WHILE_BLOCKED_ON_MUTEX 0)
  set(PAL_PTRACE "ptrace((cmd), (pid), (void*)(addr), (data))")
  set(BSD_REGS_STYLE "((reg).regs[_REG_##RR])")
  set(HAVE_SCHED_OTHER_ASSIGNABLE 0)

elseif(CLR_CMAKE_TARGET_SUNOS)
  set(DEADLOCK_WHEN_THREAD_IS_SUSPENDED_WHILE_BLOCKED_ON_MUTEX 0)
  set(PAL_PTRACE "ptrace((cmd), (pid), (caddr_t)(addr), (data))")
  set(SET_SCHEDPARAM_NEEDS_PRIVS 1)
elseif(CLR_CMAKE_TARGET_HAIKU)
  # Haiku does not have ptrace.
  set(DEADLOCK_WHEN_THREAD_IS_SUSPENDED_WHILE_BLOCKED_ON_MUTEX 0)
  set(HAVE_SCHED_OTHER_ASSIGNABLE 1)
elseif(CLR_CMAKE_TARGET_BROWSER)
  set(DEADLOCK_WHEN_THREAD_IS_SUSPENDED_WHILE_BLOCKED_ON_MUTEX 0)
  set(HAVE_SCHED_OTHER_ASSIGNABLE 0)
else() # Anything else is Linux
  # LTTNG is not available on Android, so don't error out
  if(FEATURE_EVENTSOURCE_XPLAT AND NOT HAVE_LTTNG_TRACEPOINT_H)
    unset(HAVE_LTTNG_TRACEPOINT_H CACHE)
    message(FATAL_ERROR "Cannot find liblttng-ust-dev. Try installing liblttng-ust-dev  (or the appropriate packages for your platform)")
  endif()
  set(DEADLOCK_WHEN_THREAD_IS_SUSPENDED_WHILE_BLOCKED_ON_MUTEX 0)
  set(PAL_PTRACE "ptrace((cmd), (pid), (void*)(addr), (data))")
  set(HAVE_SCHED_OTHER_ASSIGNABLE 1)
endif(CLR_CMAKE_TARGET_APPLE)

check_struct_has_member(
    "struct statfs"
    f_fstypename
    "sys/mount.h"
    HAVE_STATFS_FSTYPENAME)

check_struct_has_member(
    "struct statvfs"
    f_fstypename
    "sys/mount.h"
    HAVE_STATVFS_FSTYPENAME)

# statfs: Find whether this struct exists
if (HAVE_STATFS_FSTYPENAME OR HAVE_STATVFS_FSTYPENAME)
    set (STATFS_INCLUDES sys/mount.h)
else ()
    set (STATFS_INCLUDES sys/statfs.h)
endif ()

check_prototype_definition(
    statfs
    "int statfs(const char *path, struct statfs *buf)"
    0
    ${STATFS_INCLUDES}
    HAVE_NON_LEGACY_STATFS)

configure_file(${CMAKE_CURRENT_LIST_DIR}/config.h.in ${CMAKE_CURRENT_BINARY_DIR}/config.h)
