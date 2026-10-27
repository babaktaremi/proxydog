/*
 * ProxyDog hook: redirects outbound TCP connect() calls of the host process to the local ProxyDog engine.
 *
 * For each non-loopback IPv4/IPv6 TCP connect(), the library connects to 127.0.0.1:$PROXYDOG_PORT instead and
 * sends "PDG1", family, port, address. The engine tunnels through the user's proxy and answers one byte
 * (0 = ready). If the engine is not listening, the connect fails (fail closed, traffic never goes direct).
 *
 * macOS: loaded with DYLD_INSERT_LIBRARIES, uses a __DATA,__interpose entry.
 * Linux: loaded with LD_PRELOAD, defines connect() and forwards to the next definition.
 */
#define _GNU_SOURCE
#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <unistd.h>

#ifndef __APPLE__
#include <dlfcn.h>
#endif

typedef int (*connect_fn)(int, const struct sockaddr *, socklen_t);

static int is_local(const struct sockaddr *a)
{
    if (a->sa_family == AF_INET) {
        const struct sockaddr_in *s = (const struct sockaddr_in *)a;
        uint32_t ip = ntohl(s->sin_addr.s_addr);
        return (ip >> 24) == 127 || ip == 0;
    }
    const struct sockaddr_in6 *s6 = (const struct sockaddr_in6 *)a;
    if (IN6_IS_ADDR_LOOPBACK(&s6->sin6_addr) || IN6_IS_ADDR_UNSPECIFIED(&s6->sin6_addr))
        return 1;
    if (IN6_IS_ADDR_V4MAPPED(&s6->sin6_addr)) {
        const uint8_t *b = s6->sin6_addr.s6_addr;
        return b[12] == 127 || (b[12] | b[13] | b[14] | b[15]) == 0;
    }
    return 0;
}

static int write_all(int fd, const void *buf, size_t n)
{
    const uint8_t *p = buf;
    while (n > 0) {
        ssize_t w = write(fd, p, n);
        if (w < 0) {
            if (errno == EINTR) continue;
            return -1;
        }
        p += w;
        n -= (size_t)w;
    }
    return 0;
}

/* Returns the engine port when this connection should be proxied, else 0. */
static int pd_engine_port(int fd, const struct sockaddr *addr, socklen_t len)
{
    const char *portenv = getenv("PROXYDOG_PORT");
    if (!portenv || !addr)
        return 0;

    if (addr->sa_family == AF_INET) {
        if (len < sizeof(struct sockaddr_in)) return 0;
    } else if (addr->sa_family == AF_INET6) {
        if (len < sizeof(struct sockaddr_in6)) return 0;
    } else {
        return 0;
    }

    int type = 0;
    socklen_t tl = sizeof type;
    if (getsockopt(fd, SOL_SOCKET, SO_TYPE, &type, &tl) != 0 || type != SOCK_STREAM)
        return 0;
    if (is_local(addr))
        return 0;

    int port = atoi(portenv);
    return (port > 0 && port <= 65535) ? port : 0;
}

static int pd_connect(int fd, const struct sockaddr *addr, socklen_t len, connect_fn real)
{
    int port = pd_engine_port(fd, addr, len);
    if (port == 0)
        return real(fd, addr, len);

    uint8_t hdr[7 + 16];
    size_t hlen;
    memcpy(hdr, "PDG1", 4);
    if (addr->sa_family == AF_INET) {
        const struct sockaddr_in *s = (const struct sockaddr_in *)addr;
        hdr[4] = 4;
        memcpy(hdr + 5, &s->sin_port, 2);
        memcpy(hdr + 7, &s->sin_addr, 4);
        hlen = 11;
    } else {
        const struct sockaddr_in6 *s = (const struct sockaddr_in6 *)addr;
        hdr[4] = 6;
        memcpy(hdr + 5, &s->sin6_port, 2);
        memcpy(hdr + 7, &s->sin6_addr, 16);
        hlen = 23;
    }

    /* The handshake is synchronous, so temporarily make non-blocking sockets blocking. */
    int flags = fcntl(fd, F_GETFL, 0);
    int was_nonblock = flags >= 0 && (flags & O_NONBLOCK);
    if (was_nonblock)
        fcntl(fd, F_SETFL, flags & ~O_NONBLOCK);

    struct timeval old_rcv, zero_tv = {0, 0}, limit = {30, 0};
    socklen_t tvl = sizeof old_rcv;
    if (getsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &old_rcv, &tvl) != 0)
        old_rcv = zero_tv;

    int rc;
    if (addr->sa_family == AF_INET) {
        struct sockaddr_in e;
        memset(&e, 0, sizeof e);
#ifdef __APPLE__
        e.sin_len = sizeof e;
#endif
        e.sin_family = AF_INET;
        e.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        e.sin_port = htons((uint16_t)port);
        rc = real(fd, (struct sockaddr *)&e, sizeof e);
    } else {
        /* An AF_INET6 socket reaches the IPv4 engine through the v4-mapped loopback address. */
        struct sockaddr_in6 e;
        memset(&e, 0, sizeof e);
#ifdef __APPLE__
        e.sin6_len = sizeof e;
#endif
        e.sin6_family = AF_INET6;
        e.sin6_addr.s6_addr[10] = 0xff;
        e.sin6_addr.s6_addr[11] = 0xff;
        e.sin6_addr.s6_addr[12] = 127;
        e.sin6_addr.s6_addr[15] = 1;
        e.sin6_port = htons((uint16_t)port);
        rc = real(fd, (struct sockaddr *)&e, sizeof e);
    }

    int result = -1;
    int err = errno;
    if (rc == 0) {
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &limit, sizeof limit);
        uint8_t ack = 1;
        ssize_t r = -1;
        if (write_all(fd, hdr, hlen) == 0) {
            do { r = read(fd, &ack, 1); } while (r < 0 && errno == EINTR);
        }
        if (r == 1 && ack == 0) {
            result = 0;
        } else {
            err = (r == 1) ? ECONNREFUSED : ECONNRESET;
        }
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &old_rcv, sizeof old_rcv);
    }

    if (was_nonblock)
        fcntl(fd, F_SETFL, flags);
    if (result != 0)
        errno = err;
    return result;
}

#ifdef __APPLE__

static int pd_connect_mac(int fd, const struct sockaddr *addr, socklen_t len)
{
    return pd_connect(fd, addr, len, connect);
}

#define DYLD_INTERPOSE(replacement, replacee)                                                   \
    __attribute__((used)) static struct { const void *r; const void *o; }                       \
    _interpose_##replacee __attribute__((section("__DATA,__interpose"))) = {                    \
        (const void *)(unsigned long)&replacement, (const void *)(unsigned long)&replacee }

DYLD_INTERPOSE(pd_connect_mac, connect);

/*
 * Network.framework, NSURLSession and most modern macOS apps open sockets with connectx(), not connect().
 * Same redirect; data the caller wants sent with the connection (TCP Fast Open) goes out after the handshake.
 */
#include <sys/uio.h>

static int pd_connectx_mac(int fd, const sa_endpoints_t *ep, sae_associd_t associd, unsigned int flags,
                           const struct iovec *iov, unsigned int iovcnt, size_t *len, sae_connid_t *connid)
{
    if (ep && ep->sae_dstaddr && pd_engine_port(fd, ep->sae_dstaddr, ep->sae_dstaddrlen) != 0) {
        int rc = pd_connect(fd, ep->sae_dstaddr, ep->sae_dstaddrlen, connect);
        if (rc != 0)
            return rc;
        size_t sent = 0;
        if (iov && iovcnt > 0) {
            ssize_t w = writev(fd, iov, (int)iovcnt);
            if (w > 0) sent = (size_t)w;
        }
        if (len) *len = sent;
        if (connid) *connid = SAE_CONNID_ANY;
        return 0;
    }
    return connectx(fd, ep, associd, flags, iov, iovcnt, len, connid);
}

DYLD_INTERPOSE(pd_connectx_mac, connectx);

/*
 * Network.framework (and URLSession / CFNetwork on top of it) runs its own user-space stack and never touches a BSD
 * socket, so connect()/connectx() interposition cannot see it. Apple's supported way in is a proxy configuration on
 * the default privacy context (macOS 14+). The engine already speaks SOCKS5, so register it there, with failover off
 * so a failing proxy never falls back to a direct connection.
 */
#include <dlfcn.h>

typedef void *(*nw_endpoint_create_host_fn)(const char *, const char *);
typedef void *(*nw_proxy_config_create_socksv5_fn)(void *);
typedef void (*nw_proxy_config_set_failover_allowed_fn)(void *, bool);
typedef void (*nw_privacy_context_add_proxy_fn)(void *, void *);

__attribute__((constructor)) static void pd_register_network_framework_proxy(void)
{
    const char *port = getenv("PROXYDOG_PORT");
    if (!port)
        return;
    void *nw = dlopen("/System/Library/Frameworks/Network.framework/Network", RTLD_LAZY);
    if (!nw)
        return;

    nw_endpoint_create_host_fn make_endpoint = (nw_endpoint_create_host_fn)dlsym(nw, "nw_endpoint_create_host");
    nw_proxy_config_create_socksv5_fn make_socks = (nw_proxy_config_create_socksv5_fn)dlsym(nw, "nw_proxy_config_create_socksv5");
    nw_privacy_context_add_proxy_fn add_proxy = (nw_privacy_context_add_proxy_fn)dlsym(nw, "nw_privacy_context_add_proxy");
    void **default_context = (void **)dlsym(nw, "_nw_privacy_context_default_context");
    if (!make_endpoint || !make_socks || !add_proxy || !default_context || !*default_context)
        return; /* macOS older than 14: libc interposition is all there is */

    void *endpoint = make_endpoint("127.0.0.1", port);
    void *config = endpoint ? make_socks(endpoint) : NULL;
    if (!config)
        return;
    nw_proxy_config_set_failover_allowed_fn no_failover =
        (nw_proxy_config_set_failover_allowed_fn)dlsym(nw, "nw_proxy_config_set_failover_allowed");
    if (no_failover)
        no_failover(config, false);
    add_proxy(*default_context, config);
}

#else

int connect(int fd, const struct sockaddr *addr, socklen_t len)
{
    static connect_fn real;
    if (!real)
        real = (connect_fn)dlsym(RTLD_NEXT, "connect");
    return pd_connect(fd, addr, len, real);
}

#endif
