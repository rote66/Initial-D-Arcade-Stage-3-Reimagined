#include <mach/mach.h>
#include <sys/resource.h>

// Process CPU: 100% is one core, as in the supplied NFS3 Metrics implementation.
extern "C" double Idas3ProcessCpuSeconds()
{
    rusage usage{};
    if (getrusage(RUSAGE_SELF, &usage) != 0) return -1;
    return usage.ru_utime.tv_sec + usage.ru_stime.tv_sec
        + (usage.ru_utime.tv_usec + usage.ru_stime.tv_usec) / 1000000.0;
}

extern "C" double Idas3ProcessMemoryMiB()
{
    task_vm_info_data_t info{};
    mach_msg_type_number_t count = TASK_VM_INFO_COUNT;
    if (task_info(mach_task_self(), TASK_VM_INFO, reinterpret_cast<task_info_t>(&info), &count) != KERN_SUCCESS
        || count < TASK_VM_INFO_REV1_COUNT) return -1;
    return info.phys_footprint / (1024.0 * 1024.0);
}
