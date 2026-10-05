"""隔离配置库验收工具：只读导入断言与测试库故障注入，不输出密钥。"""
import json
from contextlib import closing
import sqlite3
import sys
from pathlib import Path


def load(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def read_config(path):
    with closing(sqlite3.connect(Path(path).resolve().as_uri() + "?mode=ro", uri=True, timeout=5)) as db:
        rows = dict(db.execute("SELECT key, value FROM config"))
    assert set(rows) == {"proxy", "common", "other", "preparations"}, "配置行不完整"
    values = {key: json.loads(value) for key, value in rows.items()}
    return dict(proxy=values["proxy"], commonConfig=values["common"],
                otherConfig=values["other"], preparations=values["preparations"])


def subset(expected, actual, path="config"):
    # 新版本可以补充缺省字段，但导入源已有的业务字段不得丢失或改值。
    if isinstance(expected, dict):
        assert isinstance(actual, dict), path + " 对象类型错误"
        for key, value in expected.items():
            if key in ("balance_query", "balanceQuery"):
                continue
            if value is None and key not in actual:
                continue
            assert key in actual, path + "." + key + " 缺失"
            subset(value, actual[key], path + "." + key)
    elif isinstance(expected, list):
        assert isinstance(actual, list) and len(expected) == len(actual), path + " 列表长度变化"
        for i, (left, right) in enumerate(zip(expected, actual)):
            subset(left, right, path + "[" + str(i) + "]")
    else:
        assert expected == actual, path + " 值变化"


def verify_import(db_path, source_path):
    expected, actual = load(source_path), read_config(db_path)
    # 主程序按已确认规则自启已有供应商的通道，忽略旧启停标记。
    # 此处核验稳定配置；启停字段的纯存储往返另由 ConfigServiceTests 覆盖。
    for route in expected["proxy"]["routes"]:
        route.pop("desired_running", None)
    subset(expected, actual)
    user = Path(db_path).parent
    migrated = user / "config.json.migrated.bak"
    assert not (user / "config.json").exists(), "原配置未改名"
    assert migrated.read_bytes() == Path(source_path).read_bytes(), "迁移原文件被改写"
    backups = list((user / "backup").glob("config_*.json.bak"))
    assert any(p.read_bytes() == migrated.read_bytes() for p in backups), "缺少导入前原样备份"
    print("PASS: 四个配置节点、原文件改名及导入前备份")


def verify_identity(db_path, original_path):
    original, current = load(original_path), read_config(db_path)
    for field in ("selected_route_id", "providers"):
        subset(original["proxy"][field], current["proxy"][field], "proxy." + field)
    assert len(original["proxy"]["routes"]) == len(current["proxy"]["routes"]), "通道数量变化"
    for left, right in zip(original["proxy"]["routes"], current["proxy"]["routes"]):
        for field in ("id", "name", "client_type", "local_token", "current_provider_id", "current_key_id"):
            if field in left:
                assert left[field] == right[field], "通道 " + field + " 变化"
    for left, right in zip(original.get("preparations", []), current["preparations"]):
        for field in ("id", "clientType", "providerSource", "providerId", "keyId", "apiKey", "model", "idleMinutes"):
            if field in left:
                assert left[field] == right[field], "准备任务 " + field + " 变化"
    assert len(original.get("preparations", [])) == len(current["preparations"]), "准备任务数量变化"
    print("PASS: 通道、供应商、密钥与准备任务身份完整")


if __name__ == "__main__":
    action, db_path, *rest = sys.argv[1:]
    if action == "read":
        print(json.dumps(read_config(db_path), ensure_ascii=True))
    elif action == "verify-import":
        verify_import(db_path, rest[0])
    elif action == "verify-identity":
        verify_identity(db_path, rest[0])
    elif action in ("missing-row", "invalid-json"):
        with closing(sqlite3.connect(db_path)) as db, db:
            if action == "missing-row":
                db.execute("DELETE FROM config WHERE key = 'preparations'")
            else:
                db.execute("UPDATE config SET value = 'broken-json' WHERE key = 'common'")
    else:
        raise SystemExit("未知验收操作")
