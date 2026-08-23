PREFIX ?= /usr/local
BINDIR ?= $(HOME)/.local/bin
TARGET = clipbridge

.PHONY: all build install uninstall clean

all: build

build:
	@mkdir -p bin
	swiftc -O -o bin/$(TARGET) main.swift

install: build
	@mkdir -p $(BINDIR)
	install -m 755 bin/$(TARGET) $(BINDIR)/$(TARGET)
	@$(BINDIR)/$(TARGET) start

uninstall:
	@if [ -f $(BINDIR)/$(TARGET) ]; then \
		$(BINDIR)/$(TARGET) stop; \
		rm -f $(BINDIR)/$(TARGET); \
	fi

clean:
	rm -rf bin
