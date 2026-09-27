TARGET = clipbridge

ifeq ($(OS),Windows_NT)
    # Windows configuration
    BINDIR ?= $(USERPROFILE)/.local/bin
    CSC ?= C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe
    EXE_EXT = .exe
    RM = del /Q /F
    MKDIR = if not exist bin mkdir bin
else
    # macOS / Unix configuration
    PREFIX ?= /usr/local
    BINDIR ?= $(HOME)/.local/bin
    EXE_EXT =
    RM = rm -rf
    MKDIR = mkdir -p bin
endif

.PHONY: all build install uninstall clean

all: build

build:
ifeq ($(OS),Windows_NT)
	@$(MKDIR)
	"$(CSC)" /target:winexe /optimize+ /platform:anycpu /out:bin/$(TARGET)$(EXE_EXT) Program.cs
else
	@$(MKDIR)
	swiftc -O -o bin/$(TARGET)$(EXE_EXT) main.swift
endif

install: build
ifeq ($(OS),Windows_NT)
	@if not exist "$(BINDIR)" mkdir "$(BINDIR)"
	copy /Y bin\$(TARGET)$(EXE_EXT) "$(BINDIR)\$(TARGET)$(EXE_EXT)"
	"$(BINDIR)\$(TARGET)$(EXE_EXT)" start
else
	@mkdir -p $(BINDIR)
	install -m 755 bin/$(TARGET) $(BINDIR)/
	$(BINDIR)/$(TARGET) start
endif

clean:
ifeq ($(OS),Windows_NT)
	-@$(RM) bin\$(TARGET)$(EXE_EXT)
else
	@$(RM) bin
endif
