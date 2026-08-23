#!/bin/bash
set -e

INSTALL_DIR="${HOME}/.local/bin"
mkdir -p "${INSTALL_DIR}"

echo "🔨 Compiling ClipBridge with Swift..."
swiftc -O -o "${INSTALL_DIR}/clipbridge" main.swift
chmod +x "${INSTALL_DIR}/clipbridge"

echo "🚀 Installing and starting background LaunchAgent daemon..."
"${INSTALL_DIR}/clipbridge" start

echo ""
echo "🎉 ClipBridge installed successfully to ${INSTALL_DIR}/clipbridge"
echo "Make sure ${INSTALL_DIR} is in your PATH."
echo "Check status with: clipbridge status"
