#!/bin/sh
# 從專案根目錄核對 Compose 實際資源，再以固定容器 ID 啟動本機重設。
set -eu

# 拒絕時只顯示固定的維護提示，讓操作者先核對資源。
fail() {
    printf '%s\n' "$1" >&2
    exit 1
}

# 只接受 Compose 專案名。既有 COMPOSE_FILE 可選擇隔離設定，實際容器仍須通過驗證。
[ "$#" -le 1 ] || fail '用法：sh reset-demo.sh [Compose 專案名]'
project=${1-procurement-approval}
case "$project" in
    ''|[!a-z0-9]*|*[!a-z0-9_-]*) fail 'Compose 專案名不合法。' ;;
esac
repo=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
cd "$repo"

# 限制使用本機 Unix socket，避免維護命令透過遠端 Docker context 執行。
docker_host=${DOCKER_HOST:-}
if [ -z "$docker_host" ] || [ -n "${DOCKER_CONTEXT:-}" ]; then
    docker_context=$(docker context show) || fail '無法確認本機 Docker context。'
    docker_host=$(docker context inspect --format '{{.Endpoints.docker.Host}}' "$docker_context") \
        || fail '無法確認本機 Docker context。'
fi
case "$docker_host" in
    unix:///*) ;;
    *) fail '展示重設僅可透過本機 Docker Unix socket 執行。' ;;
esac
# 讓查找、核對與啟動都使用同一端點，避免期間切換預設 context。
unset DOCKER_CONTEXT
export DOCKER_HOST="$docker_host"

# 只接受同一份 Compose 專案的單一正式服務容器，排除跨專案、一次性執行或混用工作目錄。
db=$(docker compose -p "$project" ps --all --quiet db) || fail '找不到展示資料庫容器。'
app=$(docker compose -p "$project" ps --all --quiet app) || fail '找不到展示網站容器。'
case "$db:$app" in
    :*|*:|*[!0-9a-f:]*) fail '請確認展示資料庫與網站各有一個容器。' ;;
esac
identity='{{index .Config.Labels "com.docker.compose.project"}}|{{index .Config.Labels "com.docker.compose.service"}}|{{index .Config.Labels "com.docker.compose.project.working_dir"}}|{{index .Config.Labels "com.docker.compose.oneoff"}}'
[ "$(docker inspect --format "$identity|{{.State.Running}}" "$db")" = "$project|db|$repo|False|true" ] \
    && [ "$(docker inspect --format "$identity" "$app")" = "$project|app|$repo|False" ] \
    || fail 'Compose 服務身分不符，或資料庫尚未啟動。'

# 核對容器實際掛載及資料卷的擁有者和儲存選項，拒絕同名卷轉接到其他位置。
volume="${project}_database"
mount='{{range .Mounts}}{{if eq .Destination "/var/lib/postgresql"}}{{.Type}}|{{.Name}}{{end}}{{end}}'
[ "$(docker inspect --format "$mount" "$db")" = "volume|$volume" ] \
    || fail '資料庫容器未掛載本專案的展示資料卷。'
volume_identity=$(docker volume inspect --format '{{index .Labels "com.docker.compose.project"}}|{{index .Labels "com.docker.compose.volume"}}|{{.Driver}}|{{.Scope}}|{{json .Options}}' "$volume") \
    || fail '無法核對本專案的展示資料卷。'
case "$volume_identity" in
    "$project|database|local|local|null"|"$project|database|local|local|{}") ;;
    *) fail '展示資料卷的擁有者或儲存選項不符。' ;;
esac

# 只容許資料卷和憑證掛載，避免另一個掛載遮蔽預設的 PostgreSQL 資料目錄。
mount_shape=$(docker inspect --format '{{len .Mounts}}|{{json .HostConfig.Tmpfs}}' "$db")
case "$mount_shape" in
    '2|null'|'2|{}') ;;
    *) fail '展示資料卷有額外或重疊的掛載。' ;;
esac
pgdata=/var/lib/postgresql/18/docker
[ "$(docker exec "$db" readlink -f "$pgdata")" = "$pgdata" ] \
    || fail '無法確認展示資料卷的預設 PostgreSQL 資料目錄。'

# 共用網站及資料庫既有的受保護憑證掛載，並拒絕尚未包含維護命令的舊映像。
local_mount='{{range .Mounts}}{{if and (eq .Destination "/local") (eq .Type "bind")}}{{.Source}}{{end}}{{end}}'
directory=$(docker inspect --format "$local_mount" "$db")
case "$directory" in
    ''|*,*) fail '找不到有效的本機憑證掛載。' ;;
esac
[ -d "$directory" ] && [ "$directory" = "$(docker inspect --format "$local_mount" "$app")" ] \
    || fail '網站與資料庫的本機憑證掛載不符。'
image=$(docker inspect --format '{{.Image}}' "$app")
[ "$(docker image inspect --format '{{index .Config.Labels "io.procurement-approval.reset-demo"}}' "$image")" = '1' ] \
    || fail '請先依展示文件建置並啟動包含重設功能的版本。'

# 鎖定剛核對的 db 容器網路和網站映像。若容器已換新，Docker 會拒絕舊 ID。
exec docker run --rm -i --pull never --network "container:$db" \
    --mount "type=bind,source=$directory,target=/local" \
    --env "MaintenanceProject=$project" "$image" reset-demo
