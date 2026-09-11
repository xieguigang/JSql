-- CSV 存储格式回归脚本
-- 用法：在 REPL 启动时加上 `--format csv`，然后依次粘贴本脚本的语句。
--      dotnet run --project src\Repl\Repl.vbproj -- --db D:\data\jsql-csv --format csv
-- 覆盖：建库建表 / 结果集校验 / 含逗号与双引号字段的转义 / 索引 / WAL checkpoint / SHOW STORAGE。

CREATE DATABASE csvreg;
USE csvreg;
CREATE TABLE annotation (
  id int unsigned NOT NULL,
  name varchar(512) NOT NULL COMMENT 'name of the metabolite',
  adducts varchar(32) NOT NULL DEFAULT '[M+H]+',
  mz double NOT NULL,
  add_time datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (id)
) COMMENT='csv regression table';
DESCRIBE annotation;
SHOW STORAGE;

-- 普通插入
INSERT INTO annotation (id, name, adducts, mz) VALUES (1, 'glucose', '[M+H]+', 180.0634);
INSERT INTO annotation (id, name, mz) VALUES (2, 'lactate', 90.0317);
INSERT INTO annotation (id, name, adducts, mz) VALUES (3, 'citrate', '[M-H]-', 191.0197);

-- 含逗号、双引号、换行的字段：CSV 需要正确转义；换行在写盘时被规范化为空格
INSERT INTO annotation (id, name, adducts, mz) VALUES (4, 'a, b "quoted"', '[M+H]+', 100.5);
INSERT INTO annotation (id, name, adducts, mz) VALUES (5, 'multi
line value', '[M+Na]+', 77.25);

SELECT * FROM annotation;
SELECT id, name, mz FROM annotation WHERE mz > 100 ORDER BY mz DESC;

-- 写入后立即 checkpoint：数据落入 .csv，.wal 清空
CHECKPOINT;
SHOW STORAGE;
SELECT COUNT(*) AS total, SUM(mz) AS sum_mz, AVG(mz) AS avg_mz FROM annotation;

-- 局部更新 / 删除（应走增量写而不是整表重写）
UPDATE annotation SET mz = 181.0 WHERE name = 'glucose';
SELECT name, mz FROM annotation WHERE name = 'glucose';
DELETE FROM annotation WHERE name = 'lactate';
SELECT COUNT(*) AS after_delete FROM annotation;

-- 物理文件校验提示：<db>\csvreg\annotation.csv 的首行应为
--   id,name,adducts,mz,add_time
-- 之后每行一条记录，含换行的字段在文件中不应出现真实的换行。
