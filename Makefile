# SourceSharp Host: the front door. `make setup` first (writes roots.props).
SHELL := /bin/bash
.SHELLFLAGS := -eo pipefail -c

DOTNET        ?= $(HOME)/.dotnet/dotnet
CONFIG        ?= Debug
MAPTOOLS_ROOT ?= ../source-sdk-map-tools
MAPTOOLS_REPO ?= https://github.com/muflub/hl2sdk_tools_sharp.git
# dev only: `make mod-image-dev` packs a mod image from its bin/release/. Not a build root.
SOURCE_SHARP_ROOT ?= ../source-sdk-sharp

KUBECONFIG_FILE ?= $(CURDIR)/local-k3s.yaml
KUBECTL       ?= KUBECONFIG=$(KUBECONFIG_FILE) kubectl
NAMESPACE     ?= descent
REGISTRY      ?= localhost:30500
TAG           ?= $(shell git rev-parse --short HEAD)
IMAGE_TOOL    ?= podman
TIER          ?= fake
MOD_IMAGE     ?=
MOD_NAME      ?= descent
# The 64-bit libraries anonymous 244310 lacks (plan D-H11): a directory whose bin/linux64/ holds them.
# Default: the local Steam install of app 243750. Production: a licensed source (docs/ops.md).
ENGINE_FILES  ?= $(HOME)/.steam/steam/steamapps/common/Source SDK Base 2013 Multiplayer

TEST_ENV      := MSBUILDDISABLENODEREUSE=1
SLN           := Host.slnx

.PHONY: setup build test check pack publish clean run local gateway fake \
        images image-service image-gateway image-engine image-fake \
        cluster-up cluster-down deploy undeploy admin logs mod-image-dev \
        lint-rooms bake link live-h0 live-h4 live-h5 live-h6 live-h7 live-h9 maptools-clean

setup:
	@if [ ! -d "$(MAPTOOLS_ROOT)/.git" ]; then git clone "$(MAPTOOLS_REPO)" "$(MAPTOOLS_ROOT)"; fi
	@root=$$(cd "$(MAPTOOLS_ROOT)" && pwd); \
	if [ -n "$$(git -C "$$root" status --porcelain)" ]; then \
	  echo "refusing: $$root is dirty (this repo never builds against uncommitted map tools)"; \
	  git -C "$$root" status --porcelain | head; exit 1; fi; \
	printf '<Project>\n  <PropertyGroup>\n    <MapToolsRoot>%s/</MapToolsRoot>\n  </PropertyGroup>\n</Project>\n' "$$root" > roots.props; \
	echo "MapToolsRoot = $$root at $$(git -C "$$root" log --oneline -1)"
	$(DOTNET) restore $(SLN) --nologo -v q

build:
	$(TEST_ENV) $(DOTNET) build $(SLN) --nologo -m:1 -c $(CONFIG) -v q
	@$(MAKE) --no-print-directory maptools-clean

test:
	$(TEST_ENV) $(DOTNET) test $(SLN) --nologo -m:1 -c $(CONFIG)
	@$(MAKE) --no-print-directory maptools-clean

check: build test

# We never write into a root: after any build its porcelain status is empty.
maptools-clean:
	@root=$$(sed -n 's#.*<MapToolsRoot>\(.*\)</MapToolsRoot>.*#\1#p' roots.props); \
	if [ -n "$$(git -C "$$root" status --porcelain)" ]; then echo "MapToolsRoot $$root is dirty after the build"; exit 1; fi

# PACK_VERSION=1.2.3 stamps the packages (CI passes the release tag); unset, the projects' default.
PACK_VERSION  ?=
pack:
	$(DOTNET) pack src/Host.Contracts/Host.Contracts.csproj -c Release -o bin/nupkg --nologo -v q $(if $(PACK_VERSION),-p:Version=$(PACK_VERSION))
	$(DOTNET) pack src/Host.Sdk/Host.Sdk.csproj -c Release -o bin/nupkg --nologo -v q $(if $(PACK_VERSION),-p:Version=$(PACK_VERSION))
	DOTNET=$(DOTNET) deploy/pack-check.sh

# The Linux build: linux-x64 publishes of the executables, tarred into bin/dist (CI uploads these).
publish:
	DOTNET=$(DOTNET) BUILD_VERSION=$(TAG) deploy/publish-linux.sh
	@$(MAKE) --no-print-directory maptools-clean

run:
	$(DOTNET) run --project src/Descent.Service

# D-H13: the real service as one local process; the game connects with -hostlocal $(LOCAL_DIR)/local-host.json
LOCAL_DIR ?= $(CURDIR)/bin/local
local:
	$(DOTNET) run --project src/Descent.Service -- --local "$(LOCAL_DIR)"
gateway:
	$(DOTNET) run --project src/Host.Gateway
fake:
	$(DOTNET) run --project src/Host.FakeGame

lint-rooms bake link:
	$(DOTNET) run --project src/Descent.Service -- $@ $(ARGS)

# ---- images (podman or buildah; docker works too with IMAGE_TOOL=docker) ----
images: image-service image-gateway image-engine image-fake
image-service image-gateway image-fake: image-%:
	@root=$$(sed -n 's#.*<MapToolsRoot>\(.*\)</MapToolsRoot>.*#\1#p' roots.props); \
	$(IMAGE_TOOL) build -f deploy/Dockerfile.$* --build-context maptools="$$root" \
	  --build-arg BUILD_VERSION=$(TAG) -t $(REGISTRY)/descent-$*:$(TAG) .
	$(IMAGE_TOOL) push --tls-verify=false $(REGISTRY)/descent-$*:$(TAG)

image-engine:
	@root=$$(sed -n 's#.*<MapToolsRoot>\(.*\)</MapToolsRoot>.*#\1#p' roots.props); \
	[ -d "$(ENGINE_FILES)/bin/linux64" ] || { echo "ENGINE_FILES=$(ENGINE_FILES) has no bin/linux64 (docs/ops.md)"; exit 1; }; \
	$(IMAGE_TOOL) build -f deploy/Dockerfile.engine --build-context maptools="$$root" \
	  --build-context enginefiles="$(ENGINE_FILES)" \
	  --build-arg BUILD_VERSION=$(TAG) -t $(REGISTRY)/descent-engine:$(TAG) .
	$(IMAGE_TOOL) push --tls-verify=false $(REGISTRY)/descent-engine:$(TAG)

mod-image-dev:
	deploy/mod-image-dev.sh "$(SOURCE_SHARP_ROOT)" "$(REGISTRY)/descent-mod:dev"

# ---- cluster (local k3s through $(KUBECONFIG_FILE); never a cluster someone else uses) ----
cluster-up:
	deploy/cluster-up.sh "$(KUBECONFIG_FILE)" "$(NAMESPACE)" "$(MOD_IMAGE)" "$(TIER)"
cluster-down:
	$(KUBECTL) delete namespace $(NAMESPACE) --wait=true
OVERLAY       := deploy/k8s/$(if $(filter fake,$(TIER)),local-fake,local)
CONTENT_PATH  ?= $(HOME)/.steam/steam/steamapps/common/Team Fortress 2
deploy:
	$(KUBECTL) kustomize $(OVERLAY) | sed -e "s#:latest#:$(TAG)#g" -e "s#__CONTENT_PATH__#$(CONTENT_PATH)#" | $(KUBECTL) apply -f -
	@if [ -n "$(MOD_IMAGE)" ]; then \
	  ref="$(MOD_IMAGE)"; reg=$${ref%%/*}; rest=$${ref#*/}; name=$${rest%%:*}; tag=$${rest##*:}; \
	  $(KUBECTL) -n $(NAMESPACE) set env statefulset/descent-service \
	    DESCENT_Instances__ModImage__Registry="$$reg" DESCENT_Instances__ModImage__Name="$$name" DESCENT_Instances__ModImage__Tag="$$tag" \
	    DESCENT_Mod__Name="$(MOD_NAME)"; fi
	$(KUBECTL) -n $(NAMESPACE) rollout status statefulset/descent-service --timeout=180s
	$(KUBECTL) -n $(NAMESPACE) rollout status deploy/descent-gateway --timeout=180s
undeploy:
	$(KUBECTL) kustomize $(OVERLAY) | sed -e "s#__CONTENT_PATH__#$(CONTENT_PATH)#" | $(KUBECTL) delete --ignore-not-found --wait=true -f -
	$(KUBECTL) -n $(NAMESPACE) wait --for=delete pod -l app=descent-game --timeout=90s || true
admin:
	$(KUBECTL) -n $(NAMESPACE) port-forward pod/descent-service-0 5000:5000
logs:
	$(KUBECTL) -n $(NAMESPACE) logs -f statefulset/descent-service

live-h0 live-h4 live-h5 live-h6 live-h7 live-h9: live-%:
	TIER=$(TIER) KUBECONFIG=$(KUBECONFIG_FILE) NAMESPACE=$(NAMESPACE) live/$*.sh

clean:
	rm -rf bin
