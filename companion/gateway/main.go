package main

import (
	"crypto/tls"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"log"
	"net/http"
	"os"
	"time"
)

type Config struct {
	Listen      string `json:"listen"`
	TLSCertFile string `json:"tlsCertFile"`
	TLSKeyFile  string `json:"tlsKeyFile"`
	BearerToken string `json:"bearerToken"`
	RulesFile   string `json:"rulesFile"`
}

func main() {
	configPath := flag.String("config", os.Getenv("KILLSWITCH_GATEWAY_CONFIG"), "path to the gateway configuration file")
	flag.Parse()
	if *configPath == "" {
		log.Fatal("set -config or KILLSWITCH_GATEWAY_CONFIG")
	}
	config, err := loadConfig(*configPath)
	if err != nil {
		log.Fatal(err)
	}
	if config.BearerToken == "" {
		log.Fatal("gateway bearerToken must be configured")
	}
	if config.TLSCertFile == "" || config.TLSKeyFile == "" {
		log.Fatal("TLS certificate and key are required; the gateway does not serve plaintext HTTP")
	}
	if config.Listen == "" {
		config.Listen = ":9443"
	}
	rules, err := loadRules(config.RulesFile)
	if err != nil {
		log.Fatal(err)
	}
	server := &http.Server{
		Addr:              config.Listen,
		Handler:           &Gateway{Token: config.BearerToken, Rules: rules, RulesFile: config.RulesFile},
		ReadHeaderTimeout: 5 * time.Second,
		IdleTimeout:       30 * time.Second,
		TLSConfig: &tls.Config{
			MinVersion: tls.VersionTLS12,
			NextProtos: []string{"http/1.1"},
		},
	}
	log.Printf("Kill Switch request gateway listening on %s", config.Listen)
	if err := server.ListenAndServeTLS(config.TLSCertFile, config.TLSKeyFile); err != nil && !errors.Is(err, http.ErrServerClosed) {
		log.Fatal(fmt.Errorf("gateway stopped: %w", err))
	}
}

func loadRules(path string) ([]Rule, error) {
	if path == "" {
		return nil, nil
	}
	file, err := os.Open(path)
	if errors.Is(err, os.ErrNotExist) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	defer file.Close()
	var rules []Rule
	decoder := json.NewDecoder(file)
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&rules); err != nil {
		return nil, fmt.Errorf("read rules file: %w", err)
	}
	for _, rule := range rules {
		if err := validateRule(rule); err != nil {
			return nil, fmt.Errorf("invalid rule %q: %w", rule.Target, err)
		}
	}
	return rules, nil
}

func loadConfig(path string) (Config, error) {
	file, err := os.Open(path)
	if err != nil {
		return Config{}, err
	}
	defer file.Close()
	var config Config
	decoder := json.NewDecoder(file)
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&config); err != nil {
		return Config{}, fmt.Errorf("read gateway config: %w", err)
	}
	return config, nil
}
