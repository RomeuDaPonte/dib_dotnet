# Fake NAS for the NAS adapter tests (PLAN.md §5.1): OpenSSH with password login for the user "webapp", the folders
# /share/Orçamentos and /share/Obras, and a login shell that records every command sshd is asked to run.
# Never a copy of the real NAS: no real credentials, no real data. Built by tests/Shared/NasFake.cs.
FROM alpine:3.22

RUN apk add --no-cache openssh-server \
 && ssh-keygen -q -t ed25519 -N '' -f /etc/ssh/ssh_host_ed25519_key \
 && adduser -D -h /home/ssh-user -s /usr/local/bin/recording-sh webapp \
 && echo 'webapp:fake-nas-password' | chpasswd \
 && mkdir -p /share/Orçamentos /share/Obras /var/log/nas \
 && chown -R webapp /share /var/log/nas \
 && printf '%s\n' \
      'HostKey /etc/ssh/ssh_host_ed25519_key' \
      'PasswordAuthentication yes' \
      'KbdInteractiveAuthentication no' \
      'PermitRootLogin no' \
      'AllowUsers webapp' \
      > /etc/ssh/sshd_config.d/nas-fake.conf

COPY recording-sh /usr/local/bin/recording-sh
RUN chmod 755 /usr/local/bin/recording-sh

EXPOSE 22
CMD ["/usr/sbin/sshd", "-D", "-e"]
