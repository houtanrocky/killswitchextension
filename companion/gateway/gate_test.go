package main

import (
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

type roundTripFunc func(*http.Request) (*http.Response, error)

func (f roundTripFunc) RoundTrip(request *http.Request) (*http.Response, error) { return f(request) }

func TestBlocksMismatchedSourceBeforeUpstream(t *testing.T) {
	forwardCount := 0
	gateway := &Gateway{
		Token: "secret",
		Rules: []Rule{{Target: "example.com/private", IPConditions: "203.0.113.10|"}},
		RoundTripper: roundTripFunc(func(*http.Request) (*http.Response, error) {
			forwardCount++
			return nil, nil
		}),
	}
	recorder := request(t, gateway, "198.51.100.7:53210", "203.0.113.10", `{"url":"https://example.com/private/data","method":"GET"}`)
	if recorder.Code != http.StatusUnavailableForLegalReasons {
		t.Fatalf("status = %d, want 451", recorder.Code)
	}
	if forwardCount != 0 {
		t.Fatalf("upstream received %d requests, want none", forwardCount)
	}
}

func TestUsesSocketSourceInsteadOfForwardedHeader(t *testing.T) {
	gateway := &Gateway{Token: "secret", Rules: []Rule{{Target: "example.com", IPConditions: "203.0.113.10"}}}
	recorder := request(t, gateway, "198.51.100.7:53210", "203.0.113.10", `{"url":"https://example.com/","method":"GET"}`)
	if recorder.Code != http.StatusUnavailableForLegalReasons {
		t.Fatalf("spoofed X-Forwarded-For bypassed source-IP check: status %d", recorder.Code)
	}
}

func TestAllowsMatchingSourceAndForwardsRequest(t *testing.T) {
	forwarded := false
	gateway := &Gateway{
		Token: "secret",
		Rules: []Rule{{Target: "example.com/private", IPConditions: "203.0.113.10"}},
		RoundTripper: roundTripFunc(func(request *http.Request) (*http.Response, error) {
			forwarded = true
			if request.URL.String() != "https://example.com/private/data" {
				t.Errorf("forwarded URL = %q", request.URL)
			}
			if request.Method != http.MethodPost {
				t.Errorf("forwarded method = %q", request.Method)
			}
			return &http.Response{
				StatusCode: http.StatusCreated,
				Header:     make(http.Header),
				Body:       io.NopCloser(strings.NewReader("created")),
			}, nil
		}),
	}
	recorder := request(t, gateway, "203.0.113.10:53210", "", `{"url":"https://example.com/private/data","method":"POST","body":"aGVsbG8="}`)
	if recorder.Code != http.StatusCreated || recorder.Body.String() != "created" {
		t.Fatalf("response = %d %q", recorder.Code, recorder.Body.String())
	}
	if !forwarded {
		t.Fatal("matching request was not forwarded")
	}
}

func TestEvaluatesEveryFetchUsingItsObservedPeerAddress(t *testing.T) {
	forwardCount := 0
	gateway := &Gateway{
		Token: "secret",
		Rules: []Rule{{Target: "example.com", IPConditions: "203.0.113.10"}},
		RoundTripper: roundTripFunc(func(*http.Request) (*http.Response, error) {
			forwardCount++
			return &http.Response{StatusCode: http.StatusNoContent, Header: make(http.Header), Body: io.NopCloser(strings.NewReader(""))}, nil
		}),
	}
	allowed := request(t, gateway, "203.0.113.10:53210", "", `{"url":"https://example.com/first","method":"GET"}`)
	blocked := request(t, gateway, "198.51.100.7:53210", "", `{"url":"https://example.com/second","method":"GET"}`)
	if allowed.Code != http.StatusNoContent || blocked.Code != http.StatusUnavailableForLegalReasons {
		t.Fatalf("fetch statuses = %d, %d; want 204, 451", allowed.Code, blocked.Code)
	}
	if forwardCount != 1 {
		t.Fatalf("upstream received %d requests, want only the allowed request", forwardCount)
	}
}

func TestDenyListWinsAndOnlyBlocksListedAddress(t *testing.T) {
	if !shouldBlockForIP("203.0.113.10", []string{"203.0.113.10", "!203.0.113.10"}) {
		t.Fatal("deny entry did not override allow entry")
	}
	if shouldBlockForIP("198.51.100.20", []string{"!203.0.113.10"}) {
		t.Fatal("deny-only rule blocked an unlisted address")
	}
}

func TestMatchesPagePathsWithoutPrefixCollisions(t *testing.T) {
	private, _ := http.NewRequest(http.MethodGet, "https://example.com/private/child", nil)
	query, _ := http.NewRequest(http.MethodGet, "https://example.com/private?view=full", nil)
	neighbor, _ := http.NewRequest(http.MethodGet, "https://example.com/privately", nil)
	if !ruleMatches("https://example.com/private", private.URL) {
		t.Fatal("page rule did not match child path")
	}
	if ruleMatches("https://example.com/private", neighbor.URL) {
		t.Fatal("page rule matched a neighboring path")
	}
	if !ruleMatches("https://example.com/private?view=full", query.URL) {
		t.Fatal("page rule did not match its query string")
	}
}

func TestWildcardHostMatchesRootAndSubdomainsOnly(t *testing.T) {
	root, _ := http.NewRequest(http.MethodGet, "https://example.com/path", nil)
	subdomain, _ := http.NewRequest(http.MethodGet, "https://a.example.com/path", nil)
	neighbor, _ := http.NewRequest(http.MethodGet, "https://notexample.com/path", nil)
	if !ruleMatches("*.example.com", root.URL) || !ruleMatches("*.example.com", subdomain.URL) {
		t.Fatal("wildcard host did not match root and subdomain")
	}
	if ruleMatches("*.example.com", neighbor.URL) {
		t.Fatal("wildcard host matched a suffix collision")
	}
}

func TestRejectsUnauthorizedRequest(t *testing.T) {
	forwarded := false
	gateway := &Gateway{
		Token: "secret",
		RoundTripper: roundTripFunc(func(*http.Request) (*http.Response, error) {
			forwarded = true
			return nil, nil
		}),
	}
	recorder := httptest.NewRecorder()
	request := httptest.NewRequest(http.MethodPost, "/v1/fetch", strings.NewReader(`{"url":"https://example.com/"}`))
	request.RemoteAddr = "203.0.113.10:12345"
	gateway.ServeHTTP(recorder, request)
	if recorder.Code != http.StatusUnauthorized || forwarded {
		t.Fatalf("unauthorized request result: code=%d forwarded=%v", recorder.Code, forwarded)
	}
}

func TestClientIpEndpointReportsSocketPeerAndRequiresAuthentication(t *testing.T) {
	gateway := &Gateway{Token: "secret"}
	request := httptest.NewRequest(http.MethodGet, "/v1/ip", nil)
	request.RemoteAddr = "198.51.100.44:53210"
	request.Header.Set("Authorization", "Bearer secret")
	request.Header.Set("X-Forwarded-For", "203.0.113.99")
	response := httptest.NewRecorder()
	gateway.ServeHTTP(response, request)
	if response.Code != http.StatusOK || response.Body.String() != "198.51.100.44" {
		t.Fatalf("client IP response = %d %q", response.Code, response.Body.String())
	}
	if response.Header().Get("Connection") != "close" {
		t.Fatal("client IP endpoint should close each request connection")
	}

	unauthorized := httptest.NewRecorder()
	gateway.ServeHTTP(unauthorized, httptest.NewRequest(http.MethodGet, "/v1/ip", nil))
	if unauthorized.Code != http.StatusUnauthorized {
		t.Fatalf("unauthenticated client IP status = %d, want 401", unauthorized.Code)
	}
}

func TestHttpsGatewayRoundTripsAllowedRequest(t *testing.T) {
	forwarded := false
	gateway := &Gateway{
		Token: "secret",
		Rules: []Rule{{Target: "example.com", IPConditions: "127.0.0.1"}},
		RoundTripper: roundTripFunc(func(request *http.Request) (*http.Response, error) {
			forwarded = true
			if request.URL.String() != "https://example.com/" {
				t.Errorf("upstream URL = %q", request.URL)
			}
			return &http.Response{
				StatusCode: http.StatusOK,
				Header:     make(http.Header),
				Body:       io.NopCloser(strings.NewReader("upstream response")),
			}, nil
		}),
	}
	server := httptest.NewTLSServer(gateway)
	defer server.Close()
	client := server.Client()
	client.Transport.(*http.Transport).DisableKeepAlives = true
	request, err := http.NewRequest(http.MethodPost, server.URL+"/v1/fetch", strings.NewReader(`{"url":"https://example.com/","method":"GET"}`))
	if err != nil {
		t.Fatal(err)
	}
	request.Header.Set("Authorization", "Bearer secret")
	response, err := client.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	body, _ := io.ReadAll(response.Body)
	if response.StatusCode != http.StatusOK || string(body) != "upstream response" || !forwarded {
		t.Fatalf("HTTPS gateway result: status=%d body=%q forwarded=%v", response.StatusCode, body, forwarded)
	}
}

func TestPolicyEndpointPersistsValidUpdates(t *testing.T) {
	rulesFile := filepath.Join(t.TempDir(), "rules.json")
	gateway := &Gateway{Token: "secret", RulesFile: rulesFile}
	policyRequest := httptest.NewRequest(http.MethodPut, "/v1/policy", strings.NewReader(`[{"target":"example.com","ipConditions":"203.0.113.10"}]`))
	policyRequest.Header.Set("Authorization", "Bearer secret")
	policyResponse := httptest.NewRecorder()
	gateway.ServeHTTP(policyResponse, policyRequest)
	if policyResponse.Code != http.StatusNoContent {
		t.Fatalf("policy status = %d, want 204: %s", policyResponse.Code, policyResponse.Body.String())
	}
	data, err := os.ReadFile(rulesFile)
	if err != nil {
		t.Fatal(err)
	}
	var stored []Rule
	if err := json.Unmarshal(data, &stored); err != nil || len(stored) != 1 {
		t.Fatalf("persisted policy = %s, err=%v", data, err)
	}
	secondPolicy := httptest.NewRequest(http.MethodPut, "/v1/policy", strings.NewReader(`[{"target":"example.net","ipConditions":"198.51.100.20"}]`))
	secondPolicy.Header.Set("Authorization", "Bearer secret")
	secondResponse := httptest.NewRecorder()
	gateway.ServeHTTP(secondResponse, secondPolicy)
	if secondResponse.Code != http.StatusNoContent || gateway.currentRules()[0].Target != "example.net" {
		t.Fatalf("replacement policy failed: status=%d rules=%+v", secondResponse.Code, gateway.currentRules())
	}

	invalidRequest := httptest.NewRequest(http.MethodPut, "/v1/policy", strings.NewReader(`[{"target":"example.net","ipConditions":"invalid"}]`))
	invalidRequest.Header.Set("Authorization", "Bearer secret")
	invalidResponse := httptest.NewRecorder()
	gateway.ServeHTTP(invalidResponse, invalidRequest)
	if invalidResponse.Code != http.StatusBadRequest || len(gateway.currentRules()) != 1 {
		t.Fatalf("invalid policy altered active policy: status=%d rules=%d", invalidResponse.Code, len(gateway.currentRules()))
	}
}

func request(t *testing.T, gateway *Gateway, remoteIP, forwardedIP, body string) *httptest.ResponseRecorder {
	t.Helper()
	recorder := httptest.NewRecorder()
	request := httptest.NewRequest(http.MethodPost, "/v1/fetch", strings.NewReader(body))
	request.RemoteAddr = remoteIP
	request.Header.Set("Authorization", "Bearer secret")
	if forwardedIP != "" {
		request.Header.Set("X-Forwarded-For", forwardedIP)
	}
	gateway.ServeHTTP(recorder, request)
	return recorder
}
