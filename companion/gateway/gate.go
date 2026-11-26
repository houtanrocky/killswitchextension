package main

import (
	"bytes"
	"crypto/subtle"
	"encoding/json"
	"io"
	"net"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"sync"
)

type Rule struct {
	Target       string `json:"target"`
	IPConditions string `json:"ipConditions"`
}

type FetchRequest struct {
	URL     string              `json:"url"`
	Method  string              `json:"method"`
	Headers map[string][]string `json:"headers,omitempty"`
	Body    []byte              `json:"body,omitempty"`
}

type Gateway struct {
	Token        string
	Rules        []Rule
	RulesFile    string
	RoundTripper http.RoundTripper
	mu           sync.RWMutex
}

func (g *Gateway) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	if r.URL.Path == "/v1/ip" {
		g.serveClientIP(w, r)
		return
	}
	if r.URL.Path == "/v1/policy" {
		g.servePolicy(w, r)
		return
	}
	if r.URL.Path != "/v1/fetch" || r.Method != http.MethodPost {
		http.NotFound(w, r)
		return
	}
	if g.Token == "" || !validBearer(r.Header.Get("Authorization"), g.Token) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	// A new remote TCP peer address is evaluated for each proxied request.
	r.Close = true
	w.Header().Set("Connection", "close")

	var input FetchRequest
	decoder := json.NewDecoder(http.MaxBytesReader(w, r.Body, 16<<20))
	if err := decoder.Decode(&input); err != nil {
		http.Error(w, "invalid request", http.StatusBadRequest)
		return
	}
	target, err := url.ParseRequestURI(input.URL)
	if err != nil || (target.Scheme != "http" && target.Scheme != "https") || target.Host == "" {
		http.Error(w, "invalid target URL", http.StatusBadRequest)
		return
	}

	clientIP := remoteIP(r.RemoteAddr)
	for _, rule := range g.currentRules() {
		if ruleMatches(rule.Target, target) && shouldBlockForIP(clientIP, parseIPs(rule.IPConditions)) {
			http.Error(w, "Access limited by Kill Switch", http.StatusUnavailableForLegalReasons)
			return
		}
	}

	method := input.Method
	if method == "" {
		method = http.MethodGet
	}
	upstreamRequest, err := http.NewRequestWithContext(r.Context(), method, target.String(), bytes.NewReader(input.Body))
	if err != nil {
		http.Error(w, "invalid request", http.StatusBadRequest)
		return
	}
	for name, values := range input.Headers {
		if strings.EqualFold(name, "Host") || isHopByHopHeader(name) || strings.ContainsAny(name, "\r\n") {
			continue
		}
		for _, value := range values {
			if !strings.ContainsAny(value, "\r\n") {
				upstreamRequest.Header.Add(name, value)
			}
		}
	}
	transport := g.RoundTripper
	if transport == nil {
		transport = http.DefaultTransport
	}
	response, err := transport.RoundTrip(upstreamRequest)
	if err != nil {
		http.Error(w, "upstream unavailable", http.StatusBadGateway)
		return
	}
	defer response.Body.Close()
	for name, values := range response.Header {
		if !isHopByHopHeader(name) {
			for _, value := range values {
				w.Header().Add(name, value)
			}
		}
	}
	w.WriteHeader(response.StatusCode)
	_, _ = io.Copy(w, response.Body)
}

func (g *Gateway) serveClientIP(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	if g.Token == "" || !validBearer(r.Header.Get("Authorization"), g.Token) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	clientIP := remoteIP(r.RemoteAddr)
	if clientIP == "" {
		http.Error(w, "client IP unavailable", http.StatusServiceUnavailable)
		return
	}
	r.Close = true
	w.Header().Set("Connection", "close")
	w.Header().Set("Content-Type", "text/plain; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	_, _ = io.WriteString(w, clientIP)
}

func (g *Gateway) servePolicy(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPut {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	r.Close = true
	w.Header().Set("Connection", "close")
	if g.Token == "" || !validBearer(r.Header.Get("Authorization"), g.Token) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	if g.RulesFile == "" {
		http.Error(w, "policy updates are disabled", http.StatusServiceUnavailable)
		return
	}
	var rules []Rule
	decoder := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4<<20))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&rules); err != nil {
		http.Error(w, "invalid policy", http.StatusBadRequest)
		return
	}
	for _, rule := range rules {
		if err := validateRule(rule); err != nil {
			http.Error(w, err.Error(), http.StatusBadRequest)
			return
		}
	}
	encoded, err := json.MarshalIndent(rules, "", "  ")
	if err != nil {
		http.Error(w, "could not encode policy", http.StatusInternalServerError)
		return
	}
	if err := os.MkdirAll(filepath.Dir(g.RulesFile), 0700); err != nil {
		http.Error(w, "could not save policy", http.StatusInternalServerError)
		return
	}
	tempPath := g.RulesFile + ".tmp"
	if err := os.WriteFile(tempPath, encoded, 0600); err != nil {
		http.Error(w, "could not save policy", http.StatusInternalServerError)
		return
	}
	if err := os.Rename(tempPath, g.RulesFile); err != nil {
		_ = os.Remove(tempPath)
		http.Error(w, "could not commit policy", http.StatusInternalServerError)
		return
	}
	g.mu.Lock()
	g.Rules = append([]Rule(nil), rules...)
	g.mu.Unlock()
	w.WriteHeader(http.StatusNoContent)
}

func (g *Gateway) currentRules() []Rule {
	g.mu.RLock()
	defer g.mu.RUnlock()
	return append([]Rule(nil), g.Rules...)
}

func validateRule(rule Rule) error {
	if strings.TrimSpace(rule.Target) == "" {
		return &policyError{"rule target cannot be empty"}
	}
	ips := parseIPs(rule.IPConditions)
	if len(ips) == 0 {
		return &policyError{"rule requires at least one IPv4 condition"}
	}
	for _, value := range ips {
		address := strings.TrimPrefix(value, "!")
		parsed := net.ParseIP(address)
		if parsed == nil || parsed.To4() == nil {
			return &policyError{"rule contains an invalid IPv4 address"}
		}
	}
	return nil
}

type policyError struct{ message string }

func (e *policyError) Error() string { return e.message }

func validBearer(header, expected string) bool {
	const prefix = "Bearer "
	if !strings.HasPrefix(header, prefix) {
		return false
	}
	provided := strings.TrimPrefix(header, prefix)
	return len(provided) == len(expected) && subtle.ConstantTimeCompare([]byte(provided), []byte(expected)) == 1
}

func remoteIP(remoteAddr string) string {
	host, _, err := net.SplitHostPort(remoteAddr)
	if err != nil {
		return ""
	}
	parsed := net.ParseIP(host)
	if parsed == nil {
		return ""
	}
	return parsed.String()
}

func parseIPs(raw string) []string {
	raw = strings.ReplaceAll(raw, "|", "")
	values := strings.Split(raw, ",")
	result := make([]string, 0, len(values))
	for _, value := range values {
		value = strings.TrimSpace(value)
		if value != "" {
			result = append(result, value)
		}
	}
	return result
}

func shouldBlockForIP(clientIP string, rules []string) bool {
	if net.ParseIP(clientIP) == nil || len(rules) == 0 {
		return true
	}
	allowExists, allowMatch, denyMatch := false, false, false
	for _, rule := range rules {
		deny := strings.HasPrefix(rule, "!")
		ip := strings.TrimPrefix(rule, "!")
		if deny {
			denyMatch = denyMatch || ip == clientIP
		} else {
			allowExists = true
			allowMatch = allowMatch || ip == clientIP
		}
	}
	if denyMatch {
		return true
	}
	return allowExists && !allowMatch
}

func ruleMatches(target string, requestURL *url.URL) bool {
	pattern := strings.TrimSpace(strings.ToLower(target))
	if pattern == "" {
		return false
	}
	pattern = regexp.MustCompile(`^https?://`).ReplaceAllString(pattern, "")
	candidate := strings.ToLower(requestURL.Host + requestURL.RequestURI())
	if strings.HasPrefix(pattern, "*.") && !strings.ContainsAny(pattern[2:], "/*?#") {
		suffix := pattern[2:]
		host := strings.ToLower(requestURL.Hostname())
		return host == suffix || strings.HasSuffix(host, "."+suffix)
	}
	if pattern == strings.ToLower(requestURL.Hostname()) && !strings.ContainsAny(pattern, "/*?#") {
		return true
	}
	if !strings.ContainsAny(pattern, "*?#") && !strings.Contains(pattern, "/") {
		return pattern == strings.ToLower(requestURL.Hostname())
	}
	if strings.Contains(pattern, "*") {
		parts := strings.Split(pattern, "*")
		expression := "^" + regexp.QuoteMeta(parts[0])
		for _, part := range parts[1:] {
			expression += ".*" + regexp.QuoteMeta(part)
		}
		matched, err := regexp.MatchString(expression+"$", candidate)
		return err == nil && matched
	}
	return candidate == pattern || strings.HasPrefix(candidate, pattern+"/") || strings.HasPrefix(candidate, pattern+"?")
}

func isHopByHopHeader(name string) bool {
	switch strings.ToLower(name) {
	case "connection", "proxy-connection", "keep-alive", "proxy-authenticate", "proxy-authorization", "te", "trailer", "transfer-encoding", "upgrade":
		return true
	default:
		return false
	}
}
