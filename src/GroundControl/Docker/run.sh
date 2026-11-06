# Runs a test process within the tests container: sh run.sh <directory of the test run> <command>...
#
# Writes the process ID of the command first to its standard error, so the debugger attaches to it, and kills it once the lease
# of the test run (the file lease in its directory, which the test host renews) has not changed for 15 seconds, so the tests of a test host
# which has died do not hold the ports of their fixtures. Every process started is waited for, so none is left to the first process
# of the container, which may not reap it (e.g., sleep infinity without an init process). The directory is removed once the command completes.

run=$1; shift
"$@" & tests=$!
echo $tests >&2
(
    trap 'kill $nap 2> /dev/null; wait $nap; exit' TERM
    last=
    while :; do
        sleep 15 & nap=$!
        wait $nap
        current=$(cat "$run/lease" 2> /dev/null)
        [ "$current" != "$last" ] || { kill $tests 2> /dev/null; exit; }
        last=$current
    done
) &
watchdog=$!
wait $tests; status=$?
kill $watchdog 2> /dev/null; wait $watchdog
rm -rf "$run"
exit $status
