TOP=$(realpath $(CURDIR)/../../..)
-include Make.config

escape_quote = $(subst ",\",$(1))

ifneq ($(V),)
MSBUILD_ARGS+=/p:MonoVerboseBuild=true
MSBUILD_ARGS+=-bl
endif

MSBUILD_ARGS+=/p:KeepNativeSymbols=true

DOTNET=$(TOP)/dotnet.sh
JSVU=$(HOME)/.jsvu
CHROMEDRIVER?=$(HOME)/.chromedriver
GECKODRIVER?=$(HOME)/.geckodriver

EMSDK_PATH?=$(TOP)/src/mono/browser/emsdk
CONFIG?=Release
BINDIR?=$(TOP)/artifacts/bin
OBJDIR?=$(TOP)/artifacts/obj
_MSBUILD_WASM_BUILD_ARGS=/p:TargetOS=browser /p:TargetArchitecture=wasm /p:Configuration=$(CONFIG)
XHARNESS_BROWSER?=chrome
HELIX_TARGET_QUEUE?=Ubuntu.1804.Amd64.Open

LOG_PATH=$(TOP)/artifacts/log/$(CONFIG)

all: build-all

#
# EMSCRIPTEN SETUP
#
#  If EMSDK_PATH is not set by the caller, download and setup a local emsdk install.
#

EMSCRIPTEN_VERSION := $(shell cat $(TOP)/src/mono/browser/emscripten-version.txt)
EMSDK_LOCAL_PATH=$(CURDIR)/emsdk
EMCC=source $(EMSDK_PATH)/emsdk_env.sh 2>/dev/null && emcc

.stamp-wasm-install-and-select-$(EMSCRIPTEN_VERSION):
	rm -rf $(EMSDK_LOCAL_PATH)
	git clone https://github.com/emscripten-core/emsdk.git $(EMSDK_LOCAL_PATH)
	cd $(EMSDK_LOCAL_PATH) && ./emsdk install $(EMSCRIPTEN_VERSION)
	cd $(EMSDK_LOCAL_PATH) && ./emsdk activate $(EMSCRIPTEN_VERSION)
	python3 ./sanitize.py $(EMSDK_LOCAL_PATH)
	touch $@

provision-wasm: .stamp-wasm-install-and-select-$(EMSCRIPTEN_VERSION)
	@echo "----------------------------------------------------------"
	@echo "Installed emsdk into EMSDK_PATH=$(TOP)/src/mono/browser/emsdk"

MONO_OBJ_DIR=$(OBJDIR)/mono/browser.wasm.$(CONFIG)
BUILDS_OBJ_DIR=$(MONO_OBJ_DIR)/wasm

clean-emsdk:
	$(RM) -rf $(EMSDK_LOCAL_PATH)

#
# Helper targets
#

.PHONY: runtime
.PHONY: build

build:
	EMSDK_PATH=$(EMSDK_PATH) $(TOP)/build.sh mono+libs.pretest -os browser -c $(CONFIG) --binaryLog /p:ContinueOnError=false /p:StopOnFirstFailure=true $(MSBUILD_ARGS)

build-all:
	EMSDK_PATH=$(EMSDK_PATH) $(TOP)/build.sh mono+libs -os browser -c $(CONFIG) --binaryLog /p:ContinueOnError=false /p:StopOnFirstFailure=true $(MSBUILD_ARGS)

runtime:
	EMSDK_PATH=$(EMSDK_PATH) $(TOP)/build.sh mono.runtime+mono.wasmruntime+libs.native+libs.pretest -os browser -c $(CONFIG) /p:ContinueOnError=false /p:StopOnFirstFailure=true $(MSBUILD_ARGS)

# Rebuild only the mono runtime+cross compiler, don't build dotnet.wasm
mono-runtime:
	EMSDK_PATH=$(EMSDK_PATH) $(TOP)/build.sh mono.runtime+libs.native+libs.pretest -os browser -c $(CONFIG) /p:ContinueOnError=false /p:StopOnFirstFailure=true $(MSBUILD_ARGS)

corlib:
	EMSDK_PATH=$(EMSDK_PATH) $(TOP)/build.sh mono.corelib+mono.wasmruntime+libs.pretest -os browser -c $(CONFIG) /p:ContinueOnError=false /p:StopOnFirstFailure=true $(MSBUILD_ARGS)

test-runner:
	$(DOTNET) build $(TOP)/src/libraries/Common/tests/WasmTestRunner /p:Configuration=$(CONFIG) $(MSBUILD_ARGS)

app-builder:
	$(DOTNET) build $(TOP)/src/tasks/WasmAppBuilder

build-tasks:
	$(DOTNET) build $(TOP)/src/tasks/tasks.proj /p:Configuration=$(CONFIG) $(MSBUILD_ARGS)

build-packages:
	rm -f $(TOP)/artifacts/packages/$(CONFIG)/Shipping/*.nupkg
	EMSDK_PATH=$(EMSDK_PATH) $(TOP)/build.sh mono.packages+mono.manifests+packs.product -os browser -c $(CONFIG) --binaryLog /p:ContinueOnError=false /p:StopOnFirstFailure=true $(MSBUILD_ARGS)

clean:
	$(RM) -rf $(BUILDS_OBJ_DIR)

run-tests-v8-%:
	EMSDK_PATH=$(EMSDK_PATH) PATH="$(JSVU):$(PATH)" $(DOTNET) build $(TOP)/src/libraries/$*/tests/ /t:Test $(_MSBUILD_WASM_BUILD_ARGS) /p:JSEngine=V8 $(MSBUILD_ARGS)
run-tests-sm-%:
	EMSDK_PATH=$(EMSDK_PATH) PATH="$(JSVU):$(PATH)" $(DOTNET) build $(TOP)/src/libraries/$*/tests/ /t:Test $(_MSBUILD_WASM_BUILD_ARGS) /p:JSEngine=SpiderMonkey $(MSBUILD_ARGS)
run-tests-jsc-%:
	EMSDK_PATH=$(EMSDK_PATH) PATH="$(JSVU):$(PATH)" $(DOTNET) build $(TOP)/src/libraries/$*/tests/ /t:Test $(_MSBUILD_WASM_BUILD_ARGS) /p:JSEngine=JavaScriptCore $(MSBUILD_ARGS)

run-tests-%:
	EMSDK_PATH=$(EMSDK_PATH) PATH="$(JSVU):$(PATH)" $(DOTNET) build $(TOP)/src/libraries/$*/tests/ /t:Test $(_MSBUILD_WASM_BUILD_ARGS) $(MSBUILD_ARGS)

run-build-tests:
	PATH="$(JSVU):$(PATH)" $(DOTNET) build $(TOP)/src/mono/wasm/Wasm.Build.Tests/ /t:Test /bl:$(LOG_PATH)/Wasm.Build.Tests.binlog /p:Configuration=$(CONFIG) $(_MSBUILD_WASM_BUILD_ARGS) $(MSBUILD_ARGS)

run-browser-tests-%:
	PATH="$(GECKODRIVER):$(CHROMEDRIVER):$(PATH)" XHARNESS_COMMAND="test-browser --browser=$(XHARNESS_BROWSER)" $(DOTNET) build $(TOP)/src/libraries/$*/tests/ /t:Test $(_MSBUILD_WASM_BUILD_ARGS) $(MSBUILD_ARGS)

build-runtime-tests:
	$(TOP)/src/tests/build.sh -mono os browser wasm $(CONFIG) $(MSBUILD_ARGS)

submit-wbt-helix:
	PATH="$(JSVU):$(PATH)" \
		$(DOTNET) build $(TOP)/src/mono/wasm/Wasm.Build.Tests/ /v:m /p:ArchiveTests=true /t:ArchiveTests $(_MSBUILD_WASM_BUILD_ARGS) $(MSBUILD_ARGS) && \
	EMSDK_PATH=$(EMSDK_PATH) BUILD_REASON=wasm-test SYSTEM_TEAMPROJECT=public BUILD_REPOSITORY_NAME=dotnet/runtime BUILD_SOURCEBRANCH=main \
		$(TOP)/eng/common/msbuild.sh --ci -restore $(TOP)/src/libraries/sendtohelix.proj \
		/p:TestRunNamePrefixSuffix=WasmBuildTests /p:HelixBuild=`date "+%Y%m%d.%H%M"` /p:Creator=`whoami` \
		/bl:$(LOG_PATH)/SendToHelix.binlog -v:m -p:HelixTargetQueue=$(HELIX_TARGET_QUEUE) \
		/p:RuntimeFlavor=mono /p:TargetRuntimeIdentifier= /p:MonoForceInterpreter= /p:TestScope=innerloop \
		/p:_Scenarios=buildwasmapps \
		$(_MSBUILD_WASM_BUILD_ARGS) \
		$(MSBUILD_ARGS)

submit-tests-helix:
	echo "\n** This will submit all the available test zip files to helix **\n"
	EMSDK_PATH=$(EMSDK_PATH) BUILD_REASON=wasm-test SYSTEM_TEAMPROJECT=public BUILD_REPOSITORY_NAME=dotnet/runtime BUILD_SOURCEBRANCH=main \
		$(TOP)/eng/common/msbuild.sh --ci -restore $(TOP)/src/libraries/sendtohelix.proj \
		/p:TestRunNamePrefixSuffix=WasmTests /p:HelixBuild=`date "+%Y%m%d.%H%M"` /p:Creator=`whoami` \
		/bl:$(LOG_PATH)/SendToHelix.binlog -v:n -p:HelixTargetQueue=$(HELIX_TARGET_QUEUE) \
		/p:RuntimeFlavor=mono /p:TargetRuntimeIdentifier= /p:MonoForceInterpreter= /p:TestScope=innerloop \
		$(_MSBUILD_WASM_BUILD_ARGS) \
		$(MSBUILD_ARGS)

build-dbg-proxy:
	$(DOTNET) build $(TOP)/src/mono/browser/debugger/BrowserDebugHost $(MSBUILD_ARGS)
build-app-host:
	$(DOTNET) build $(TOP)/src/mono/wasm/host $(_MSBUILD_WASM_BUILD_ARGS) $(MSBUILD_ARGS)

patch-deterministic:
	cd emsdk/emscripten/ && patch -p1 < ../../../runtime/deterministic.diff
