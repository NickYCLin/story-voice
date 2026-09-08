"""Send synthetic OTLP/JSON to a local test stack and verify Prometheus ingestion."""

import argparse
import json
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid


def read_json(url):
    with urllib.request.urlopen(url, timeout=5) as response:
        return json.load(response)


def wait_for(read, check):
    deadline = time.monotonic() + 60
    while time.monotonic() < deadline:
        try:
            result = read()
            if check(result):
                return result
        except urllib.error.URLError:
            pass
        time.sleep(0.5)
    raise AssertionError("Local metrics stack did not become ready or expose the expected samples")


def attribute(key, value):
    return {"key": key, "value": {"stringValue": value}}


def samples(instance, count, service="storyvoice-worker", scope="StoryVoice.BlueMagpie", name="storyvoice.bluemagpie.attempts"):
    now = time.time_ns()
    point = {
        "attributes": [attribute("outcome", "success"), attribute("owner", "synthetic-do-not-export")],
        "startTimeUnixNano": str(now - 5_000_000_000),
        "timeUnixNano": str(now),
        "asInt": str(count),
    }
    return {
        "resource": {"attributes": [
            attribute("service.name", service), attribute("service.instance.id", instance),
            attribute("book", "synthetic-do-not-export"),
        ]},
        "scopeMetrics": [{
            "scope": {"name": scope, "attributes": [attribute("source", "synthetic-do-not-export")]},
            "metrics": [{
                "name": name, "unit": "{attempt}",
                "sum": {"dataPoints": [point], "aggregationTemporality": 2, "isMonotonic": True},
            }],
        }],
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--otlp", default="http://127.0.0.1:15318")
    parser.add_argument("--prometheus", default="http://127.0.0.1:19090")
    args = parser.parse_args()
    for endpoint in (args.otlp, args.prometheus):
        uri = urllib.parse.urlsplit(endpoint)
        if uri.scheme != "http" or uri.hostname not in ("127.0.0.1", "::1") or uri.username or uri.password or uri.path or uri.query or uri.fragment:
            parser.error("Synthetic metrics are restricted to an HTTP loopback origin")

    wait_for(lambda: read_json(args.prometheus + "/api/v1/status/buildinfo"), lambda result: result.get("status") == "success")
    prefix = "synthetic-metrics-" + uuid.uuid4().hex
    first, second = prefix + "-one", prefix + "-two"
    payload = {"resourceMetrics": [
        samples(first, 3), samples(second, 5),
        samples(prefix + "-wrong-service", 99, service="synthetic-other-service"),
        samples(prefix + "-wrong-scope", 99, scope="Synthetic.OtherMeter"),
        samples(prefix + "-wrong-name", 99, name="synthetic.other.metric"),
    ]}
    request = urllib.request.Request(args.otlp + "/v1/metrics", json.dumps(payload).encode(),
                                     {"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(request, timeout=5) as response:
        if response.status != 200:
            raise AssertionError("Collector rejected the synthetic OTLP request")

    query = urllib.parse.urlencode({"query": '{instance=~"' + prefix + '.*"}'})
    data = wait_for(lambda: read_json(args.prometheus + "/api/v1/query?" + query),
                    lambda result: len([item for item in result["data"]["result"]
                                        if item["metric"]["__name__"] == "storyvoice_bluemagpie_attempts_total"]) >= 2)
    series = data["data"]["result"]
    if "synthetic-do-not-export" in json.dumps(data):
        raise AssertionError("An extra resource, scope or datapoint attribute escaped the filter")
    if {item["metric"]["instance"] for item in series} != {first, second}:
        raise AssertionError("A foreign service, scope or metric name escaped the filter")
    values = {item["metric"]["instance"]: float(item["value"][1]) for item in series
              if item["metric"]["__name__"] == "storyvoice_bluemagpie_attempts_total"}
    if values != {first: 3, second: 5}:
        raise AssertionError("Distinct Worker cumulative counters were merged or changed")
    print("OTLP ingestion passed: two separate counters; foreign metrics and extra attributes excluded")


if __name__ == "__main__":
    main()
