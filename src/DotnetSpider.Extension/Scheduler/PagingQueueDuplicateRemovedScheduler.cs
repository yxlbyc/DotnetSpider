﻿using Dapper;
using DotnetSpider.Core;
using DotnetSpider.Core.Infrastructure.Database;
using DotnetSpider.Core.Scheduler;
using Polly;
using Polly.Retry;
using Serilog;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace DotnetSpider.Extension.Scheduler
{
	public abstract class PagingQueueDuplicateRemovedScheduler : QueueDuplicateRemovedScheduler
	{
		private readonly string _description;
		private readonly bool _reset;
		private readonly string databaseName = "dotnetspider";
		private readonly string pagingRecordTableName = "`dotnetspider`.`paging_record`";
		private readonly string pagingTableName = "`dotnetspider`.`paging`";
		private readonly string pagingRunningTableName = "`dotnetspider`.`paging_running`";
		private string _taskName;
		private Site _site;
		private int currentPage = 0;
		protected readonly int _size;

		private readonly RetryPolicy _retryPolicy = Policy.Handle<Exception>().Retry(10000, (ex, count) =>
		{
			Log.Logger.Error($"PushRequests failed [{count}]: {ex}");
		});

		public PagingQueueDuplicateRemovedScheduler(int size, bool reset, string description = null) : this("", size, reset, description)
		{
		}

		public PagingQueueDuplicateRemovedScheduler(string taskName, int size, bool reset, string description = null)
		{
			_taskName = taskName;
			_size = size;
			_description = description;
			_reset = reset;
		}

		protected virtual IDbConnection CreateDbConnection()
		{
			return Env.DataConnectionStringSettings.CreateDbConnection();
		}

		protected abstract long GetTotalCount(IDbConnection conn);

		protected abstract IEnumerable<Request> GenerateRequest(IDbConnection conn, int page);

		public override Request Poll()
		{
			var request = base.Poll();

			if (request == null)
			{
				LoadRequests();
			}
			return request;
		}

		public override void Init(ISpider spider)
		{
			base.Init(spider);

			if (string.IsNullOrWhiteSpace(_taskName))
			{
				_taskName = spider.Name;
			}
			_site = spider.Site;

			using (var conn = CreateDbConnection())
			{
				conn.Execute($"create database if not exists {databaseName}");
				conn.Execute($"create table if not exists {pagingRecordTableName}(`identity` varchar(50) NOT NULL,`description` varchar(50) DEFAULT NULL, `creation_date` timestamp DEFAULT CURRENT_TIMESTAMP, PRIMARY KEY(`identity`))");
				conn.Execute($"create table if not exists {pagingTableName}(page int(11) NOT null, `task_name` varchar(60) NOT null, `creation_date` timestamp DEFAULT CURRENT_TIMESTAMP, PRIMARY KEY(`page`,`task_name`))");
				conn.Execute($"create table if not exists {pagingRunningTableName}(page int(11) NOT null, `task_name` varchar(60) NOT null, `creation_date` timestamp DEFAULT CURRENT_TIMESTAMP, PRIMARY KEY(`page`,`task_name`))");

				var exist = conn.QueryFirst<int>($"SELECT count(*) from {pagingRecordTableName} where `identity` = @Identity", new { spider.Identity });
				if (exist == 0)
				{
					var affected = conn.Execute($"INSERT INTO {pagingRecordTableName}(`identity`,`description`) VALUES (@Identity, @Description)", new { spider.Identity, Description = _description });
					if (affected > 0 && _reset)
					{
						conn.Execute($"delete from {pagingTableName} where `task_name` = @t", new { t = _taskName });
						conn.Execute($"delete from {pagingRunningTableName} where `task_name` = @t", new { t = _taskName });

						var totalCount = GetTotalCount(conn);
						var pageCount = totalCount / _size + (totalCount % _size > 0 ? 1 : 0);
						var pages = new List<dynamic>();
						for (var page = 1; page <= pageCount; page++)
						{
							pages.Add(new { p = page, t = _taskName });
							if (pages.Count >= 1000 || page >= pageCount)
							{
								conn.Execute($"INSERT INTO {pagingTableName}(page,`task_name`) values (@p,@t)", pages);
								pages.Clear();
							}
						}
					}
				}
			}

			LoadRequests();
		}

		private void LoadRequests()
		{
			if (currentPage > 0)
			{
				Log.Logger.Information($"Paging: {currentPage}.");
			}

			_retryPolicy.Execute(() =>
			{
				using (var conn = CreateDbConnection())
				{
					if (currentPage > 0)
					{
						if (conn.Execute($"DELETE FROM {pagingRunningTableName} where page = @p and `task_name`=@t;", new { p = currentPage, t = _taskName }) > 0)
						{
							currentPage = 0;
						}
					}

					//获取分页
					var page = conn.QueryFirstOrDefault<int>($"select page from {pagingTableName} where `task_name` = @t limit 1", new { t = _taskName });
					if (page > 0)
					{
						var tablePage = new { p = page, t = _taskName };

						var affected = conn.Execute($"DELETE FROM {pagingTableName} where page = @p and `task_name`=@t;", tablePage);
						if (affected > 0)
						{
							conn.Execute($"INSERT IGNORE INTO {pagingRunningTableName} (page,`task_name`) values(@p,@t)", tablePage);

							var requests = GenerateRequest(conn, page);

							if (!requests.Any())
							{
								conn.Execute($"DELETE FROM {pagingTableName} where page = @p and `task_name`=@t;", tablePage);
							}
							else
							{
								currentPage = page;

								foreach (var request in requests)
								{
									request.Site = request.Site == null ? _site : request.Site;
									Push(request);
								}
							}
						}
					}
				}
			});
		}
	}
}
