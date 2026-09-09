#!/usr/bin/env python3
import socket
import sys


HOST = "10.10.252.31"
PORT = 25


def read_reply(stream):
    first = stream.readline().decode("utf-8", "replace").rstrip("\r\n")
    if len(first) < 3 or not first[:3].isdigit():
        raise RuntimeError(f"invalid SMTP reply: {first!r}")
    code = int(first[:3])
    lines = [first]
    while len(lines[-1]) > 3 and lines[-1][3] == "-":
        lines.append(stream.readline().decode("utf-8", "replace").rstrip("\r\n"))
    return code, lines


def command(stream, value):
    stream.write((value + "\r\n").encode("ascii"))
    stream.flush()
    return read_reply(stream)


def require(code, expected, operation, lines):
    if code not in expected:
        raise RuntimeError(f"{operation} returned {code}: {' | '.join(lines)}")


def main():
    with socket.create_connection((HOST, PORT), timeout=5) as connection:
        connection.settimeout(5)
        with connection.makefile("rwb", buffering=0) as stream:
            code, lines = read_reply(stream)
            require(code, {220}, "greeting", lines)
            code, lines = command(stream, "EHLO deployment-check.invalid")
            require(code, {250}, "EHLO", lines)
            code, lines = command(stream, "MAIL FROM:<deployment-check@example.invalid>")
            require(code, {250}, "MAIL FROM", lines)
            code, lines = command(
                stream,
                "RCPT TO:<deployment-check-nonexistent@validation.email.digitalwarehouse.io>",
            )
            require(code, {250}, "owned-domain RCPT TO", lines)
            command(stream, "RSET")
            code, lines = command(stream, "MAIL FROM:<deployment-check@example.invalid>")
            require(code, {250}, "second MAIL FROM", lines)
            code, lines = command(stream, "RCPT TO:<outside@example.net>")
            require(code, {450, 451, 454, 550, 551, 553, 554}, "relay denial", lines)
            command(stream, "QUIT")

    for octet in range(162, 175):
        outbound_address = f"64.182.22.{octet}"
        try:
            with socket.create_connection((outbound_address, PORT), timeout=0.25):
                raise RuntimeError(
                    f"SMTP is unexpectedly listening on outbound identity {outbound_address}"
                )
        except (ConnectionRefusedError, TimeoutError, OSError):
            pass
    print("Mail-forwarder SMTP acceptance and open-relay checks passed.")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(f"ERROR: {error}", file=sys.stderr)
        raise SystemExit(1)
