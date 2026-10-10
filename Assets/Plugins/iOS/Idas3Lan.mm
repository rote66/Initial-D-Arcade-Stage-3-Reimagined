#include <ifaddrs.h>
#include <net/if.h>
#include <arpa/inet.h>
#include <cstring>

// Prefer Wi-Fi; never advertise a cellular/VPN/loopback address as a LAN room.
extern "C" int Idas3LanIPv4(char *output, int capacity)
{
    if (!output || capacity < INET_ADDRSTRLEN) return 0;
    output[0] = 0;
    ifaddrs *interfaces = nullptr;
    if (getifaddrs(&interfaces) != 0) return 0;
    for (const auto *item = interfaces; item; item = item->ifa_next) {
        if (!item->ifa_addr || item->ifa_addr->sa_family != AF_INET
            || !(item->ifa_flags & IFF_UP) || (item->ifa_flags & IFF_LOOPBACK)
            || std::strncmp(item->ifa_name, "en", 2) != 0) continue;
        const auto *address = reinterpret_cast<const sockaddr_in *>(item->ifa_addr);
        if (inet_ntop(AF_INET, &address->sin_addr, output, capacity)) {
            if (std::strcmp(item->ifa_name, "en0") == 0) break;
        }
    }
    freeifaddrs(interfaces);
    return output[0] != 0;
}
